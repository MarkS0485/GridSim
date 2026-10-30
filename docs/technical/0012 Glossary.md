# 12. Glossary

The single alphabetical reference for the terms of art this manual uses: the
power-systems vocabulary of the solver, the GB/regulatory vocabulary of the
frequency and evidence work, and the GridSim-specific coinages. Each entry
gives the definition at the level the code supports, the canonical value where
the term carries one, and the grounding symbol or file.

Family tags: **[PS]** general power-systems usage; **[GB]** a GB grid,
regulatory or NESO term; **[GS]** a GridSim-specific coinage, class name or
in-code convention.

Canonical figures used throughout (a figure that reappears with a different
value is a bug, not a variant):

| Figure | Value | Ground |
|---|---|---|
| case9 total real loss | 4.641 MW | `tests/GridSim.Tests/PowerFlowTests.cs` |
| case9 convergence | 4 iterations to 1.65e-14 pu (`gridsim case9 --once`; ch. 11 rounds it to 1.6e-14) | `README.md` (Solve a network); [technical ch. 11](<0011 Validation.md>) |
| WSCC inertia constants | H = 23.64 / 6.40 / 3.01 s | [technical ch. 11](<0011 Validation.md>) |
| WSCC total inertia | 7.465 GVA.s | [technical ch. 11](<0011 Validation.md>); `Case9InertiaTests.cs` |
| gb-full size | 3,539 buses / 5,918 branches (raw 4,240) | [ch. 2, Loading networks](<../manual/0002 Loading networks.md>) |
| gb-spine size | 316 buses (GSP level) | [ch. 2, Loading networks](<../manual/0002 Loading networks.md>); `GDA NetworkModel/emit_spine_case.py` |
| Residual worst mismatch | 1.4e-7 MVA on the six classic cases | [technical ch. 11](<0011 Validation.md>) (tolerance summary, residual row) |
| RoCoF thresholds | legacy 0.125 Hz/s, modern 1.0 Hz/s | `Core/Dynamics/FrequencyLimits.cs` |
| LFDD stage 1 / working set-point | 48.8 Hz / 49.2 Hz | `FrequencyLimits.cs`, `StabilityMetrics.cs` |
| Horizon | 169 hours (7 days + 1 h) | `Core/Diagnostics/ForwardInferenceGuard.cs:HorizonHours` |
| Bridge clock-skew allowance | none (exact 169 h horizon) | `Core/Net/GridBridgeClient.cs:HandleState` |
| Solve tolerance / iteration cap | 1e-8 pu / 30 | `Core/PowerFlow/PowerFlowOptions.cs` |
| Dense/sparse Newton threshold | always sparse on the CPU (0 buses); 200 buses is the estimator observability / GPU / solve-export crossover | `Core/PowerFlow/SolverSelector.cs:DefaultSparseThreshold`, `DenseSparseCrossoverBuses` |
| Newton step damping / Vm floor | 0.4 / 0.2 pu | `Core/PowerFlow/NewtonShell.cs:MaxUndampedStep`, `VmFloorPu` |
| Test suite | the count `dotnet test` reports (it changes with every release; not a canonical figure) | `dotnet test GridSim.slnx -c Release --no-build` |

## A

**Admittance [PS].** The reciprocal of impedance, `Y = 1/Z` (per-unit in
GridSim). A branch's series admittance is `ys = 1/(R + jX)`; real part
conductance, imaginary part susceptance. See **Y-bus** and
[02-data-model-and-per-unit](<0002 Data model and per-unit conventions.md>).

**ALoMCP [GB].** Accelerated Loss of Mains Change Programme - the GB programme
that retuned distributed-generation loss-of-mains relays from the legacy
0.125 Hz/s RoCoF setting to 1.0 Hz/s by around 2022. GridSim carries both
thresholds and the MECHANICAL tab shows margin against each. See **RoCoF
(dual threshold)**.

**Assessment [GS].** A `FrequencyAssessment` (`Core/Dynamics/FrequencyLimits.cs`):
the graded verdict on a frequency excursion: RoCoF, nadir, settle, per-limit
breach flags and a worst-first summary string. See
[05-frequency-dynamics](<0005 Frequency dynamics.md>).

**Avoided cost [GS].** The pound figure the evidence engine attaches to a
counterfactual: recorded LFDD MW shed x Value of Lost Load x outage duration
(`Core/Evidence/AvoidedCost.cs`). Computed only when the record actually shed
load and the modelled device would have held the nadir above LFDD. The VoLL
default (GBP 6,000/MWh) is a curated placeholder. See **VoLL**,
[07-evidence-engine](<0007 The evidence engine.md>).

## B

**Backward-forward sweep (BFS) [PS].** The distribution-class solver
(`Core/PowerFlow/BackwardForwardSweep.cs`) for radial and weakly-meshed
feeders; `SolverSelector` routes radial networks (one slack and
buses-1 in-service branches) to it. It carries line charging, shunts and non-unity / phase-shifting
taps exactly, draws ZIP loads at the swept voltage, and honours the flat-start, Q-limit and
cancellation options as the Newton engines do (R036). See
[03-power-flow](<0003 Power flow.md>).

**Bad data (identification) [PS].** The largest-normalised-residual loop: after
a converged fit that fails chi-square, the measurement with the largest normalised
residual is removed and the estimate re-run, pass by pass, with critical
measurements protected (`Core/Estimation/BadDataAnalysis.cs`). See **Critical
measurement**, **Prior-backfill**, [06-state-estimation](<0006 State estimation.md>).

**Base MVA [PS].** The system-wide power base every per-unit conversion is
taken against (`GridModel.BaseMva`, 100 MVA for the WSCC and GB cases). See
**Per-unit**.

**BiCGSTAB [PS].** ILU0-preconditioned biconjugate-gradient-stabilised Krylov
solver (`Core/Numerics/Sparse.cs`). Only a fallback: the Newton step uses it
when the direct sparse LU reports the Jacobian singular. A GPU sparse solve
that gives up is retried on the exact CPU LU first (1.95.009). See
[04-sparse-and-gpu-numerics](<0004 Sparse and GPU numerics.md>).

**BM - Balancing Mechanism [GB].** The near-real-time market through which
NESO accepts BIDs (turn down) and OFFERs (turn up). BM acceptances stream over
the bridge (`BalancingActionDto` with `fuelType` and `synchronous`); the RP1
tab grades each by its effect on intrinsic inertia.

**Boundary [GB].** An ETYS transmission boundary - a defined cut across the GB
network (e.g. B6, Scotland-England) with a transfer limit. Modelled as
`Boundary`/`BoundaryBranch` in Core; carried by case `boundaries.json`, by the
bridge topology's `boundaries` block, and per-period by the estimated_state
lake's `boundary_json`. See [09-gda-integration](<0009 GDA integration.md>).

**Bus / bus type [PS].** A network node (`Bus` record, `Core/Model/Model.cs`)
classified `Slack`, `Pv` or `Pq` - the classification that decides which of a
bus's four electrical quantities are specified and which are solved. See
[02-data-model-and-per-unit](<0002 Data model and per-unit conventions.md>).

## C

**Chi-square (chi-square) consistency test [PS].** The global fit test on a converged
estimate: the objective J is compared against the chi-square 95 % threshold for the
fit's degrees of freedom (`Core/Estimation/ChiSquare.cs`). PASS means the
residuals are consistent with the declared sigmas; FAIL triggers bad-data
identification. NaN without redundancy. See
[06-state-estimation](<0006 State estimation.md>).

**Chi-square pill [GS].** The per-frame chi-square verdict shown on the WPF bridge
status bar when replaying `source=estimated` - driven by the state message's
`estimate.chiPassed` (`src/GridSim.Wpf/MainWindow.xaml.cs`, bridge status pill;
`BridgeProtocol.EstimateBlock.ChiPassed`). The quality indicator a
measurement-fitted frame carries and a demand-scaled re-solve cannot.

**Comp-range [GS].** The switchable reactive range (capacitive + inductive
MVAr) of a site's compensation plant, from the ETYS Appendix B table
(`GDA NetworkModel/build_reactive_compensation.py`). A switched MSC/reactor/
SVC's state is unobservable from settlement data, so a compensated bus's Q
pseudo-measurement carries the range as extra sigma (uniform-range standard
deviation, range/sqrt12), tagged `pseudo-demand+comp-range`
(`build_estimator_measurements.py:_comp_range_by_bus`). Declaring the real
uncertainty ended the systematic QInj rejections at compensated sites.

**Convergence [PS].** Max mismatch below tolerance (default 1e-8 pu). The full
Jacobian gives quadratic convergence: case9 in 4 iterations to 1.65e-14 pu
(`gridsim case9 --once`). A non-converged or non-finite solve is reported as a failure
(`PowerFlowStatus.MaxIterations` / `NonFinite`), never as converged. See
**Convergence contract**.

**Convergence contract [GS].** `ConvergenceContract.RequireConverged`
(`Core/PowerFlow/ConvergenceContract.cs`), the first statement of every study
that starts from a solved state - fault analysis, protection assessment and
cascade, the loss-contingency sweeps, small-signal assembly, subtransient
initialisation and the RMS transient studies. A non-converged state throws
`NonConvergedStateException` naming the study, the status, the mismatch and
the iteration count, so it can never flow into a result reported as success
(1.95.010).

**Counterfactual [GS].** A "what the device *would* have done" result -
`ProvenanceTag.Counterfactual`. Never an input to another replay, and gated:
no counterfactual until the recorded baseline is reproduced with no device.
See **Reproduced-baseline gate**.

**Critical measurement [PS].** A measurement that is the sole observer of some
state direction: its residual is structurally zero, its error undetectable,
and it is protected from bad-data removal (flagged `critical` in the export;
normalised residual NaN). `se_residual_check.py --leverage` arbitrates the
flags independently.

**Crosswalk (BMU -> GSP) [GS].** The bounded table binding every Elexon BM unit
to its best GSP identity with an explicit method + confidence per row
(`GDA NetworkModel/build_bmu_crosswalk.py` ->
`resolved/bmu_crosswalk.parquet`). Ladder: name-match 0.9 (bmUnitName only) ->
strong TEC-register 0.75 (curated station codes + >=2-token matches) ->
OSUKED-nearest 0.7 -> weak TEC 0.75 -> interconnector 0.6 -> gsp-group-only
0.5 -> unmapped 0. See [09-gda-integration](<0009 GDA integration.md>).

**CSR - Compressed Sparse Row [PS].** The sparse-matrix storage of the large
solves (`CsrMatrix`, `Core/Numerics/Sparse.cs`); memory O(nnz). See
[04-sparse-and-gpu-numerics](<0004 Sparse and GPU numerics.md>).

**CuratedPlaceholder [GS].** The `ProvenanceTag` for a figure standing in for
data not yet sourced, always accompanied by a `[VERIFY]` text marker. The tag
makes a number's provisional status travel with it; preserving these markers
is required. See **[VERIFY] convention**,
[08-provenance-and-invariants](<0008 Provenance and invariants.md>).

## D

**Design basis (3,600 MW) [GS/GB].** The correlated-cluster scenario the
stability panels show - Dogger Bank A+B+C
(`CredibleLossBasis.DesignBasisLossMw`, `Core/Presentation/CredibleLossBasis.cs`),
labelled "correlated cluster (not secured)": a simultaneous event that NESO's
Frequency Risk and Control Report 2025 does not secure. It is shown beside
1,800 MW (`CredibleLossBasis.FictionLossMw`), the FRCR 2025 future largest
loss and the NETS SQSS Infrequent Infeed Loss Risk, which is the official
figure.

**Dispatch [PS].** Assignment of real-power output to generators.
`Core/Dispatch/Dispatcher.cs` applies default GB capacity factors by fuel and
balances the slack.

**DNO [GB].** Distribution Network Operator (NGED, UKPN, NPg, SPEN, SSEN,
ENWL). Their LTDS datasets are the raw material of the GDA case assembly. See
**LTDS**, [09-gda-integration](<0009 GDA integration.md>).

**Dynamic MIF [GS].** The live intrinsic-energy floor a current credible loss
needs to hold a RoCoF limit: `GVA.s = f0*deltaP/(2*RoCoF_limit)`
(`StabilityMetrics.DynamicMifGvaS`). See **MIF**.

## E

**E - stored energy [PS].** Kinetic energy in a machine's rotating mass at
synchronous speed, `E = H*MBase` (MW.s). Summed over the fleet it is the
system inertia.

**Era-mismatch [GS].** The documented reason the ETYS circuit overlay is
opt-in: ETYS 2025 describes *today's* network, so applying its real circuits
to a historical day injects reinforcements that did not exist then - the 2019
event day regressed from 46/46 chi-square-clean periods to 32/46 under the overlay.
Until the overlay is era-aware, the validated synthetic+curated spine is the
default (`GDA NetworkModel/spine/build_spine.py:apply_etys_circuits`). See
**Spine overlay**.

**Estimated source [GS].** The bridge stream source `source:"estimated"`: the
measurement-conditioned WLS lake (`Derived/estimated_state`) replayed frame by
frame, each frame carrying its own `estimate` quality block (J, chi-square verdict,
dof, removals). Contrast `presolved` (demand-scaled deterministic re-solve
lake) and `live` (static solve + per-period scalars). See
[09-gda-integration](<0009 GDA integration.md>).

**ETYS [GB].** Electricity Ten Year Statement - NESO's published network and
boundary data (boundaries + transfer limits; Appendix B circuit parameters).
Grounds the boundary bars, the spine boundary limits and the opt-in circuit
overlay.

**Evidence dossier [GS].** The `evidence` verb's umbrella output, schema
`gridsim-evidence/1`: recorded vs simulated vs counterfactual columns, inertia
risk curve, fleet comparison and avoided cost, gated on a reproduced baseline.
See [07-evidence-engine](<0007 The evidence engine.md>),
[10-file-and-wire-formats](<0010 File and wire formats.md>).

## F

**FFR - Fast Frequency Response [GB].** Sub-second active-power injection from
converter-connected resources. In the swing model FFR is a separate power
injection, never folded into stored energy E; across a staggered fleet it is
non-additive. See **Fleet dynamics**.

**Fleet dynamics [GS].** The result that a fleet of small units is not one big
device: intrinsic inertia is N-additive but staggered fast response is not, so
an equal-budget fleet reaches a deeper nadir (`Core/Fleet/FleetDynamics.cs`).

**Fold map [GS].** `fold_map.json` - the gb-full-bus -> owning-spine-bus map
(plus spine-bus -> PES group) produced by the gb-spine fold's simultaneous BFS
and written to both case directories; resolves any bus to its PES group for
per-group demand scaling (`GDA NetworkModel/emit_spine_case.py:_fold_map`).

**Forward-inference guard [GS].** The structural invariant that GridSim only
processes data at least **169 hours (7 days + 1 h)** old:
`ForwardInferenceGuard.HorizonHours = 169.0`
(`Core/Diagnostics/ForwardInferenceGuard.cs`; widened from 25 h in July 2026).
Every dated input - event onsets, measurement `takenUtc` stamps, bridge state
ticks - passes through `RejectFuture`, which throws
`ForwardInferenceException` on anything newer. Enforced by construction, not
policy. See **Horizon**, [08-provenance-and-invariants](<0008 Provenance and invariants.md>).

**Frame [GS].** A `SimFrame(Model, Result, Hour, Tick, FrequencyHz,
GenerationMw, DemandMw, ...)` - the unit the UIs bind to. `SimFrame.Model`
carries post-solve tap ratios. Live simulation, presolved replay and the
bridge all produce frames.

## G

**gb-full [GS].** The materialised GB network - 3,539 buses / 5,918 branches,
assembled from normalised DNO LTDS tables plus the transmission spine
(`GDA NetworkModel/assemble/run.py`), reduced from a raw 4,240 buses by
busbar-section deduplication. Solves to a machine-epsilon residual; the sparse
estimation target.

**gb-spine [GS].** The 316-bus GSP-level reduction of gb-full
(`emit_spine_case.py`): one bus per NESO spine node, demand/generation folded
on by the BFS owner map, sub-GSP impedances dropped by design. The network
whose granularity matches the per-GSP measurement layer one-to-one - the
default estimation target. See **Fold map**.

**GDA [GS].** Grid Data Archive - the read-only lake of GB grid data (DNO
LTDS, Elexon settlement, NESO ETYS/TEC/inertia/frequency) GridSim's GB
networks, measurements and estimated history are built from. Kept out of this
repository. See [09-gda-integration](<0009 GDA integration.md>).

**Generator [PS].** The `Generator` record (`Core/Model/Model.cs`): `Pg`/`Qg`,
setpoint `Vg`, limits `Qmin`/`Qmax`/`Pmax`, machine base `MBase`, inertia
constant `H` (dynamics only - never enters the load flow).

**gsp_ic_flows [GS].** `Derived/gsp_ic_flows` - per-period net interconnector
import at each landing GSP, from B1610 settlement volumes (`ic_mw = 2 x sum
signed MWh` of the `I_*` units). Settlement-grade and era-deep (2019+); the
estimator's preferred interconnector source, with the operator-frames columns
as fallback. See [09-gda-integration](<0009 GDA integration.md>).

**GSP [GB].** Grid Supply Point - a transmission/distribution interface
substation. The NESO anchor scheme's GSP names key the measurement layer, the
crosswalk and the spine nodes.

**GVA.s [PS].** Gigavolt-ampere-seconds - the unit of system inertia (total
stored energy / 1000). The GB inertia risk curve sweeps 260/155/120/102/50
GVA.s.

## H

**H - inertia constant [PS].** Stored kinetic energy per MVA of machine
rating, in seconds (`Generator.H`). WSCC: 23.64 / 6.40 / 3.01 s.

**Horizon [GS].** The 169-hour (7 days + 1 h) minimum lag every dated datum
must satisfy. Anything newer - recent or future - is rejected at ingestion
with a thrown `ForwardInferenceException`. The GDA side enforces the same
cutoff (`Scripts/horizon.py`; the bridge server masks every emitted tick and
rejects future starts/queries). The bridge client applies the exact horizon to streamed ticks, with no clock-skew
allowance (removed in 1.95.030). See **Forward-inference guard**.

**HVDC [PS].** High-voltage direct current - GB interconnectors and offshore
links. Converter-connected (zero intrinsic inertia); corridor-correlated
trips drive the credible loss.

## I

**ILU0 [PS].** Incomplete LU keeping the exact sparsity pattern
(`Core/Numerics/Sparse.cs`), the BiCGSTAB preconditioner.

**Inertia (intrinsic vs market/contracted) [GS].** GridSim splits system
inertia into *intrinsic* (real synchronous rotating mass, GVA.s(i)) and
*market/contracted* (converter-provided response; may include real
synchronous condensers). Only intrinsic inertia enters the purchased-time
denominator. The bridge operator block streams the stratification
(`synchronousInertiaGvaS`, `outturnInertiaGvaS`, `marketInertiaGvaS`).

**Interconnector [GB].** A cross-border HVDC link (IFA, IFA2, BritNed, Moyle,
Nemo, NSL, ElecLink, Viking, Greenlink, EastWest). Its flow enters the
estimator as a landing-GSP injection (see **gsp_ic_flows**) and its
correlated-trip status the RP1 credible loss.

**IWlsStep [GS].** The estimation-step seam
(`Core/Estimation/WlsStep.cs:IWlsStep`) - the estimator-side sibling of
`ILinearSolver`/`IPowerFlowSolver`. The estimator owns the loop (start point,
damping, convergence, chi-square, bad-data removal); the step backend owns
representation: how H is held, how the gain matrix is formed and solved, how
observability is gated, how residual covariance is computed. Implementations:
`DenseWlsStep` (up to `EstimationOptions.SparseBusThreshold`, default 200
buses), `SparseWlsStep` (CSR gain, Gilbert-Peierls LU, structure built once
per pass), `ConstrainedDenseWlsStep` (Hachtel-style KKT, `--method
constrained`), `FeasibleWlsStep` (`--method feasible`; both KKT methods are
dense only and refuse models above 3,000 buses) and `QrWlsStep`
(`EstimationMethod.Qr`, library API only - the CLI `--method` does not offer
it; orthogonal, no normal equations); robust reweighting drops in behind the same
seam. See
[06-state-estimation](<0006 State estimation.md>).

## J

**Jacobian [PS].** The matrix of partial derivatives of calculated injections
with respect to the voltage unknowns, in four polar blocks. Each Newton step
solves `J*deltax = f`. GridSim uses the full exact Jacobian. See
[03-power-flow](<0003 Power flow.md>).

## L

**LFDD [GB].** Low-Frequency Demand Disconnection - automatic demand shedding
to arrest a falling frequency; GB stage 1 arms at **48.8 Hz**
(`FrequencyLimits.LfddFirstStageHz`). An LFDD shed is a `RecordedAction`
(positive deltaP) and the substrate for avoided cost. Distinct from the 49.2 Hz
working set-point.

**LNR - largest normalised residual [PS].** The bad-data identification
statistic: `r/sqrtOmega` with Omega the residual covariance diagonal. See **Bad data**.

**LTDS [GB].** Long Term Development Statement - the DNO-published network
dataset (nodes, circuits, transformers, demand, generation) that ingestion
normalises into the per-licence tables the assembler consumes.

## M

**MATPOWER [PS].** The open power-system package whose `.m` case format and
published cases GridSim parses and validates against; case9's published
solution is the ground truth.

**MBase [PS].** A machine's own MVA rating (`Generator.MBase`), the base its H
is quoted on.

**Measurement set [GS].** A `gridsim-measurements/1` document
(`Core/Estimation/MeasurementSet.cs`): case telemetry with per-measurement
kind, value, sigma and source, addressed by bus id / branch endpoints.
Horizon-checked on load when `takenUtc` is present. See
[10-file-and-wire-formats](<0010 File and wire formats.md>).

**MIF - Minimum Intrinsic Floor [GS/GB].** The intrinsic stored energy needed
to hold a RoCoF limit for a given loss (720 GVA.s for 3,600 MW at
0.125 Hz/s). Computed live as the Dynamic MIF.

**Mismatch [PS].** Per-bus difference between specified and calculated
injection, per-unit on `BaseMva`; convergence is `max|f| < tolerance`.

## N

**Nadir [PS].** The lowest frequency a fall reaches before response arrests
it (`FrequencyAssessment.NadirHz`).

**NESO [GB].** National Energy System Operator - the GB system operator.
Source of the limit ladder, ETYS, TEC, inertia outturn and frequency traces.

**Newton-Raphson [PS].** The iterative power-flow method: linearise via the
Jacobian, solve `J*deltax = f`, step, repeat. GridSim's dense
(`NewtonRaphsonPowerFlow`) and sparse (`SparseNewtonRaphsonPowerFlow`) engines
are bit-identical on the CPU since 1.95.001: each assembles its own Y-bus,
injections and Jacobian exactly, and both take the one shared step in
`NewtonShell` (CPU direct sparse LU, damping above a 0.4 step, 0.2 pu Vm
floor). `DenseSparseParityTests` pins it. See [03-power-flow](<0003 Power flow.md>).

**N-1 [PS].** The security standard requiring survival of any single-element
loss; `nminus1`/`nminus1-sweep` screen it.

## O

**Observability [PS].** Whether the measurement set determines the whole
state. The dense backend proves gain-matrix rank and names free directions;
the sparse backend does the same up to 2,500 state variables and above that
runs the topology (island) sweep only, reporting `rank = -1`
(`SparseWlsStep.ObservabilityDiagnosisMaxState`). Unobservable runs exit 3. See
[06-state-estimation](<0006 State estimation.md>).

**OLTC [PS].** On-load tap changer (`Oltc` record): regulates a controlled bus
voltage within a deadband; the `SteadyStateSolver` outer loop moves taps on
model clones and exposes the result via `RegulatedModel`. It never steps a tap
on a non-converged or NaN solve, and `TapsSettled` is false when it stopped on
one or hit its pass cap (default 25) with taps still moving.

## P

**Per-unit (pu) [PS].** Quantities as fractions of a base: power / `BaseMva`,
voltage / `BaseKv`, impedance x S_base/V_base^2. Actual kV = `Vm x BaseKv`.

**PN fallback [GS].** The B-3 source ladder rung: when a settlement day has no
(or a partial, < 40-period) B1610 file, the per-GSP generation measurements
build from **Physical Notification** levels instead - submitted intent, not
metering - with looser sigmas (10 MW / 10 % vs B1610's 2 MW / 2 %),
`provenance = "pn"` end to end, and no group-demand sibling. Extends estimable
history to 2016-03 (`GDA NetworkModel/build_gsp_measurements.py`). See
[09-gda-integration](<0009 GDA integration.md>).

**Prior-backfill [GS].** Observability restoration on bad-data removal
(`--backfill-removals`; `EstimationOptions.BackfillRemovedFromPrior`): a
removed real-power measurement is replaced by a pseudo evaluated at the start
state (the warm start / solved prior) with a deliberately looser sigma (3x,
floored at 5 % of value), tagged `source = "removal-backfill-prior"`
(`WlsStateEstimator.BackfillSource`) and itself never removable. Stops a
removal in a weakly-redundant region letting the state run away from the
prior. Reactive kinds are deliberately not backfilled. Off by default - it
changes what "removed" means (excluded -> replaced-by-prior).

**Provenance tag [GS].** The seven-value trust enum every evidence series
carries (`Core/Diagnostics/Provenance.cs`): `Recorded`,
`DerivedFromRecorded`, `CuratedPlaceholder`, `Synthetic`, `Counterfactual`,
`Interpolated`, `UnvalidatedAtScale`. Only `Recorded` is admissible as a real
event input. See
[08-provenance-and-invariants](<0008 Provenance and invariants.md>).

**Pseudo-measurement [PS].** A prior dressed as a measurement with a
deliberately loose sigma - the folded-demand injections (`pseudo-demand`, 25 %; group-
anchored 15 %), the reactive pseudos (`pseudo-reactive`), the voltage
schedule (`pseudo-voltage-schedule`, 1.01 +/- 0.03 pu). Tagged by `source`,
never presented as telemetry. See **Virtual measurement**.

**Purchased time [GS].** Seconds from a credible loss to the 49.2 Hz working
set-point on the initial-RoCoF approximation, given intrinsic stored energy
(`StabilityMetrics.PurchasedTimeSeconds`). A physical duration: for a
3,600 MW loss, 720 GVA.s -> 6.4 s, 155 -> 1.4 s, 102 -> 0.9 s.

## Q

**Q-limit switching (PV->PQ) [PS].** The outer-loop model of a generator's
excitation ceiling: a PV bus whose aggregate reactive output violates its
limits is released to PQ with Q pinned at the binding limit, and the flow
re-solves, up to `PowerFlowOptions.MaxQLimitPasses` (default 12; below 1 is
refused). If the cap is reached right after a switch, the engines solve once
more and report the result not converged (`MaxIterations`).

## R

**Recorded [GS].** The highest-trust provenance tag - a datum directly from an
archived record; the only class admissible as a real event input.

**Reproduced-baseline gate [GS].** No counterfactual until the recorded
event's RoCoF and nadir are reproduced with no device (RoCoF +/-15 %, nadir
+/-0.15 Hz -> `Reproduced`; within twice both -> `Marginal`; else `Failed`; a
per-sample RMSE above 0.05 Hz caps the verdict at `Marginal`;
`ReplayValidator`). Anything short of `Reproduced` exits 3 and unlocks no
counterfactual. See [07-evidence-engine](<0007 The evidence engine.md>).

**Residual check [GS].** The independent NumPy instrument
(`tools/crossval/residual_check.py`) that rebuilds Y and evaluates
`S = V .* conj(Y*V)*baseMVA` at every bus. Its estimation sibling is
`se_residual_check.py` (honesty / objective / optimality / leverage). See
[11-validation](<0011 Validation.md>).

**RoCoF [PS].** Rate of change of frequency, `df/dt = f0*deltaP/(2E)` at the
instant of loss - a clean window onto inertia.

**RoCoF (dual threshold) [GB].** GB carries two loss-of-mains relay settings:
legacy **0.125 Hz/s** and modern (ALoMCP-retuned) **1.0 Hz/s**
(`FrequencyLimits`). Every assessment reports breaches against both.

## S

**Settle [PS].** The frequency the system stabilises at after primary response
deploys (`FrequencyAssessment.SettleHz`).

**Sigma (sigma) [PS].** A measurement's declared standard deviation, in its own
units; the WLS weight is 1/sigma^2. The GDA sigma models are documented per
product in [09-gda-integration](<0009 GDA integration.md>); their calibration in
[ch. 22](<../manual/0022 Estimating GB history.md>) (Calibrate the sigmas).

**Slack bus [PS].** The reference bus: voltage magnitude and angle fixed,
absorbing the system balance including losses. One per electrical island.

**SolverSelector [GS].** The single source of truth for engine choice
(`Core/PowerFlow/SolverSelector.cs`): radial -> BFS; otherwise sparse Newton,
because `DefaultSparseThreshold` is 0 and every model has more buses than that
(dense Newton only by explicit choice or a caller's own threshold; the two
Newton engines are bit-identical on the CPU). `DenseSparseCrossoverBuses` (200)
is kept for the estimator's observability report, the GPU routing and the
solve export.
`ResolveNewton` / `CreateNewton` apply the same rule without the radial check.
The CLI (`SolverBackend.Solver` / `NewtonSolver` / `Pick`), the GUI, the
protection cascade, contingency, AC-OPF, CPF, the tap optimiser, restoration,
the parameterless `SteadyStateSolver()` (REST `/solve`, viz export) and the
estimator's backend threshold all use it. The two Newton engines are
bit-identical, so the choice changes speed and memory, never a number.

**Sparse LU [PS].** Gilbert-Peierls left-looking sparse LU with partial
pivoting over a reverse Cuthill-McKee column ordering (`Core/Numerics/SparseLu.cs`,
`SparseLuFactorization.cs`; `CpuSparseLuSolver` in the power flow). The
canonical linear solve of every CPU Newton step, dense engine included.
Returns `null` (singular) when a pivot magnitude is at or below the shared
relative floor, 1e-12 x max|A| (`NumericTolerances.RelativePivotFloor`; an
absolute 1e-300 before October 2026).

**Spine [GS].** The GSP-level GB transmission backbone built by
`GDA NetworkModel/spine/build_spine.py`: one bus per deduplicated NESO GSP
site, synthetic Delaunay->Gabriel->prune+MST topology, curated corridor
overlay, boundary limits from NESO constraint files. gb-full's DNO fragments
attach to it; gb-spine is its estimation-grade reduction.

**Spine overlay (`SPINE_ETYS=1`) [GS].** The opt-in replacement of the spine's
synthetic per-km impedances with the real ETYS Appendix B circuit list
(parallel-combined per site pair, provenance `etys-appendix-b`). Off by
default because of the **era-mismatch** caveat; enable it for investigations
of the present-day network only. See **Era-mismatch**.

**Synthetic GB [GS].** The licence-free GB case (`Core/Synthetic/SyntheticGb`)
that lets the GB workflow run with no GDA lake present. Distinct from the
materialised gb-full.

**System inertia [PS].** Total stored kinetic energy,
`sum H*MBase / 1000` GVA.s (`GridModel.SystemInertiaGvaS`).

## T

**Tap ratio [PS].** A transformer's off-nominal turns ratio
(`Branch.TapRatio`, 1.0 nominal). A MATPOWER ratio of 0 parses as 1.0, and the
admittance stamp treats any ratio <= 0 as 1.0.

**TEC [GB].** Transmission Entry Capacity - the NESO register of contracted
transmission-connected capacity; a crosswalk rung and a fleet-assembly
source.

**Tolerance [PS].** The convergence threshold on the mismatch
(`PowerFlowOptions.Tolerance`, default 1e-8 pu). The estimator's is
`EstimationOptions.Tolerance` (default 1e-8) on max |deltax|.

**TSGB [GS].** Twin Scroll Grid Balancer - the device the frequency/evidence
apparatus models; `TsgbSizer` inverts the swing equation to size inertia and
FFR for target RoCoF and nadir.

## V

**[VERIFY] convention [GS].** The in-source marker that always accompanies a
`CuratedPlaceholder` figure. It describes the trust level of the software's
data and is preserved deliberately - stripping a `[VERIFY]` without sourcing
the figure defeats the integrity apparatus. See
[08-provenance-and-invariants](<0008 Provenance and invariants.md>).

**Virtual measurement [PS].** A structural zero-injection constraint at a pure
switching node, tagged `virtual-zero-injection` - soft (tight-ish sigma) under
WLS, promoted to a hard KKT equality under `--method constrained`
(`ConstrainedDenseWlsStep.ConstraintSource`).

**VoLL [GB].** Value of Lost Load, GBP/MWh of unserved demand. Default
GBP 6,000/MWh, a curated placeholder (GB estimates span GBP 6k-17k/MWh).

## W

**Warm start [PS].** Seeding a solve or estimate from a previous solution.
The assembler bakes a converged operating point into GDA cases; the estimator
accepts `--warm-start` (iterate from the case's solved point - standard EMS
practice, needed for real data) and `--prior solve` (solve the
measured-injection power flow first; its solution replaces the flat voltage
schedule as `source = "prior-solved"` and becomes the warm start -
`Core/Estimation/MeasuredInjectionPrior.cs`).

**Working set-point (49.2 Hz) [GS/GB].** The operational floor purchased time
is measured to (`StabilityMetrics.LfddWorkingSetpointHz`) - deliberately
distinct from the 48.8 Hz LFDD relay arming point.

**WSCC [PS].** The Western System Coordinating Council 9-bus system
(`IeeeCases.Case9()`), GridSim's physics ground truth.

## Y

**Y-bus [PS].** The nxn complex bus admittance matrix, `Y*V = I`. Extremely
sparse for real grids - the fact behind the CPU-first numerics
([04-sparse-and-gpu-numerics](<0004 Sparse and GPU numerics.md>)).

## Reference tables

**The GB frequency-limit ladder** (`Core/Dynamics/FrequencyLimits.cs`;
`StabilityMetrics.cs`):

| Rung | Constant | Value |
|---|---|---|
| Nominal | `NominalHz` | 50.0 Hz |
| Operational | `OperationalLow/HighHz` | 49.8 / 50.2 Hz |
| Statutory | `StatutoryLow/HighHz` | 49.5 / 50.5 Hz |
| Working set-point | `LfddWorkingSetpointHz` | 49.2 Hz |
| LFDD stage 1 | `LfddFirstStageHz` | 48.8 Hz |
| RoCoF legacy | `RocofLimitLegacyHzPerS` | 0.125 Hz/s |
| RoCoF modern | `RocofLimitModernHzPerS` | 1.0 Hz/s |

**The seven provenance tags** (`Core/Diagnostics/Provenance.cs`):

| Tag | Rule |
|---|---|
| `Recorded` | Only class admissible as a real event input |
| `DerivedFromRecorded` | Computed from records |
| `CuratedPlaceholder` | Unsourced stand-in; always carries `[VERIFY]` |
| `Synthetic` | Model-generated; never fed to the integrator as a record |
| `Counterfactual` | Device what-if; never an input to a replay |
| `Interpolated` | Gap-filled; usable for fidelity, never as an event source |
| `UnvalidatedAtScale` | Distributed-inertia result with no GB-scale reference; never presented as validated |

## Protections & asset dynamics

| Term | Meaning |
|---|---|
| Asset dynamics | Authored per-asset behaviour (grid-following/forming inverters, passive plant) contributing a `pAssetFleet` swing term; ch. 5, 13 |
| Protection stage | Monitored quantity -> threshold + dwell -> action (`TripInfeed`/`ShedDemand`/`InjectStep`/`OpenBranch`/`RestoreBranch`); ch. 15 |
| LoM / RoCoF trip | Loss-of-Mains protection tripping embedded generation on rate-of-change-of-frequency (legacy 0.125, modern 1.0 Hz/s) |
| LFDD | Low-Frequency Demand Disconnection - a demand-shed protection arming at 48.8 Hz (modelled dynamically) |
| Recloser | A protection that opens a branch (topology) and auto-recloses after a delay |
| Island partition | The pre-pass that finds connected components over in-service branches and elects a per-island slack or blacks out a dead island (`IslandPartition`) |
| Reduction gate | The invariant that an absent/never-operating fleet leaves the frequency AND power-flow solve byte-identical |

## See also

- [ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>) - the provenance and horizon apparatus in depth
- [ch. 6, State estimation](<0006 State estimation.md>) - the estimation vocabulary in its mathematical setting
- [ch. 9, GDA integration](<0009 GDA integration.md>) - the GDA-side terms (crosswalk, fold, spine, lake products)
- [ch. 10, File and wire formats](<0010 File and wire formats.md>) - the schemas behind the format names used here
- [../manual/0002 Loading networks.md](<../manual/0002 Loading networks.md>) - the case library the case-family terms refer to
