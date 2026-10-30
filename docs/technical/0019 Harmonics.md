# 19. Harmonics

This chapter specifies the harmonic-analysis subsystem in `src/GridSim.Core/Harmonics/`: the frequency-dependent
network admittance `Y(h)`, the linear penetration solve `Y(h) V(h) = I(h)`, the driving-point impedance scan and the
fine sweep with golden-section refinement, the distortion indices and compliance tables, the coupled iterative harmonic
power flow, the three-phase penetration, the harmonic state estimator and the complex linear-solver seam they share.
The command-line verb `harmonics` and its console and export formats are in
[ch. 17 of the user manual](<../manual/0017 Harmonic studies.md>); this chapter gives the mathematics and the
code behind them.

The design rests on one split. Everything that depends on the network is derived from the network model: `Y(h)` is
built from the same branch stamp as the fundamental `YBus`, the response is Kirchhoff's current law at each order, and
the indices are arithmetic over the result. The one quantity the engine cannot derive is the injected harmonic current
phasor `I_h` per bus per order, because it is a property of the connected equipment. That quantity is a first-class,
versioned, provenance-tagged input (`HarmonicSourceSet`), and every output echoes its tag. A study on assumed spectra
and a study on metered spectra have the same shape and run the same code; a bus upgrades from assumed to metered in
place.

## Model

### Notation

`h` is the harmonic order (frequency `h * f0`, with `f0 = GridModel.FrequencyHz`). `n` is the bus count. `Y(h)` is
the `n x n` complex nodal admittance matrix at order `h` in per-unit on `GridModel.BaseMva`. `I(h)` is the nodal
injection vector and `V(h)` the nodal voltage vector, in per-unit. A voltage magnitude of `1.0` is the nominal
fundamental voltage of the bus.

### Provenance of the source spectrum

Each `HarmonicSource` carries a `ProvenanceTag`.

| Tag | Meaning |
|---|---|
| `CuratedPlaceholder` | An assumed-typical spectrum from the built-in library (`HarmonicSourceLibrary`). An engineering default, not a measurement. The library values are marked `[VERIFY]` in the XML comments of `HarmonicSource.cs`; no runtime field carries that marker. |
| `Recorded` | A spectrum metered at the point of connection by a power-quality meter. |
| `DerivedFromRecorded` | A metered spectrum transformed (rescaled to another operating point, aggregated over a feeder) but traceable to metered data. |

The export's headline `provenance` is `Recorded` when every source is `Recorded` or `DerivedFromRecorded`, and
`CuratedPlaceholder` otherwise (`HarmonicExport.ToJson`). The tag is data; it changes no number in the solve.

### Source injection

A source at bus `b` with fundamental current `I1` (`FundamentalCurrentPu`) and a spectrum of components
`(order, magnitudePercent, phaseDeg)` injects, at order `h` (`HarmonicSource.InjectionAt`):

```
I_src(h) = (magnitudePercent / 100) * I1 * exp(j * phaseDeg * pi / 180)      for the component with Order == h
         = 0                                                                 when no component has Order == h
```

Several sources on one bus add as phasors (`inj[bi] += ih`). Only the first component with a given order is read.
The fundamental (`h = 1`) carries no harmonic injection: every driver reports exactly zero voltage at `h = 1`.

### The frequency-dependent admittance `Y(h)`

`FrequencyDependentYBus.Build(model, h, options)` assembles `Y(h)` as a `ComplexCsr` matrix. It is not the fundamental
`YBus` scaled by a scalar: each element scales by its own physics. Only in-service branches are stamped. The
sections below list the contribution of each element.

**Branch pi-model with tap and phase shift.** For a branch with series `R`, `X`, total line charging `B`, tap ratio
`TapRatio` and phase shift `PhaseShiftDeg`, `AdmittanceStamp.Branch(R, X, B, tap, shift, h, rFactor)` computes:

```
ys  = 1 / (R * rFactor + j * h * X)             series admittance: reactance scales with h
bc  = j * h * B / 2                             half the line charging at each end: scales with h
a   = TapRatio  (a = 1 when TapRatio <= 0)
T   = a * exp(j * s * theta)                    theta = PhaseShiftDeg in radians, s = +1 or -1 (see "Sequence")
Yff = (ys + bc) / a^2
Ytt = ys + bc
Yft = -ys / conj(T)
Ytf = -ys / T
```

The four terms are added to cells `(from,from)`, `(to,to)`, `(from,to)` and `(to,from)`. The tap ratio is independent
of `h`. The fundamental `YBus` uses the same stamp with `h = 1`, `rFactor = 1`.

**Skin effect (opt-in).** The series-resistance factor is

```
rFactor(h) = 1 + k * sqrt(max(h - 1, 0))
k = CableSkinCoefficient   when branch.Equipment equals "cable" (case-insensitive) and CableSkinCoefficient > 0
k = SkinCoefficient        otherwise
```

Both coefficients default to 0, so `rFactor = 1` in the bare builder and in the study model
(`HarmonicModelOptions.SkinFactor`).

**Bus shunts.** For a bus with `Gs`, `Bs` (MW and MVAr at 1 pu voltage):

```
Y_shunt(h) = ( Gs + j * (Bs >= 0 ? Bs * h : Bs / h) ) / BaseMva
```

The conductance is independent of `h`. A capacitor bank (`Bs > 0`) has a susceptance that grows with `h`; a reactor
(`Bs < 0`) has one that falls as `1/h`.

**Loads as damping shunts (`IncludeLoadShunts`).** A bus with `Pd`, `Qd` (MW, MVAr) adds, at 1 pu voltage:

```
g = max(Pd, 0) / BaseMva
b = Qd >= 0 ?  -(Qd / h) / BaseMva           lagging load: a reactor, |b| falls as 1/h
             :  -(Qd * h) / BaseMva          leading load (Qd < 0): a capacitor, b grows as h
Y_load(h) = g + j b
```

A net-exporting bus (`Pd < 0`) adds no conductance; a negative resistance could manufacture a resonance.

**Machine source impedance (`IncludeGeneratorSources`).** Every generator that is neither `PowerFlowOnly` nor
`IsConverterInterfaced` adds a shunt to ground

```
X_sys     = ( X2 + X_unit ) * BaseMva / MBase               MBase = BaseMva when the generator's MBase <= 0
X2        = NegativeSeqXPu when > 0, else the effective X''d (fallback GeneratorFallbackXdPu = 0.20 pu)
X_unit    = max(0, UnitTransformerXPu)
y_g(h)    = 1 / (j * h * X_sys) = -j / (h * X_sys)           no conductance
```

(`Generator.FaultSourceNegativeSeqReactancePu`, the reactance the fault studies use, so the two cannot drift apart).
No machine resistance is stamped because the data carries none; this makes resonance peaks sharper, never flatter.
Converter-interfaced plant is a controlled current source with no EMF behind a reactance and is not stamped. A
non-positive or non-finite `X_sys` throws `InvalidOperationException` naming the unit.

**Long-line (distributed-parameter) correction (`LongLineCorrection`, opt-in).** For a branch with `B != 0`, the
lumped pi is replaced by the exact equivalent pi of the transmission-line equations:

```
z   = R * rFactor + j * h * X
y   = j * h * B
gl  = sqrt(z * y)                                  the line length cancels; no length data is needed
zc  = z * sinh(gl) / gl                            zc = z when |gl| < 1e-9
ycH = (y / 2) * tanh(gl / 2) / (gl / 2)            ycH = y / 2 when |gl| < 1e-9
ys  = 1 / zc                                       ys = 0 when zc = 0
```

`AdmittanceStamp.FromAdmittance(ys, ycH, tap, s * theta)` then stamps the branch exactly as above.

### Sequence handling and the transformer phase shift

A balanced harmonic of order `h` rotates as a symmetrical-component sequence (`HarmonicSequence.Of`):

```
h mod 3 == 1   positive sequence   (7, 13, 19, ...)
h mod 3 == 2   negative sequence   (5, 11, 17, ...)
h mod 3 == 0   zero sequence       (the triplens: 3, 9, 15, ...; also 6, 12, ...)
```

A transformer phase shift `theta` (a Dy winding, a quad booster) moves the positive and negative sequence sets in
opposite directions at every frequency. With `BalancedHarmonicSequences = true`, `Y(h)` stamps `T = a*exp(+j*theta)`
for a positive-sequence order and `T = a*exp(-j*theta)` for a negative-sequence order
(`FrequencyDependentYBus.BalancedShiftSign`). A triplen is zero sequence. Its network is the zero-sequence network
(line `Z0`, transformer winding and earthing), which the positive-sequence data model does not carry. The code therefore
refuses it at every entry:

- `Build` for a triplen order throws `ArgumentException` ("Order N is a triplen (zero sequence)").
- `Build` for a non-integer order, or an order below 1, throws `ArgumentException` in balanced mode.
- `HarmonicPenetration.Solve` and `CoupledHarmonicPowerFlow.Solve` call `RefuseTriplensIfBalanced` first: any source
  whose spectrum has a triplen component with non-zero `MagnitudePercent` throws `ArgumentException` naming the bus,
  the `SourceType` and the order. The check covers every spectrum entry, whether or not the order is in the analysed set.
- A triplen order with no source current is skipped: its voltage is exactly zero and no `Y(h)` is built.
- `HarmonicImpedanceScan` skips triplen orders.

Outside balanced mode the builder is a plain positive-sequence frequency operator (`s = +1`). The impedance-stability
assembler needs that form, with signed and fractional frequencies. `BuildSequence(model, h, kind, options)` builds the
positive (`s = +1`) or negative (`s = -1`) sequence network at any positive finite `h`, fractional included, and ignores
the balanced flag; the frequency sweep uses it. A zero-sequence request throws.

Three-phase injections and unbalanced networks are the three-phase penetration (below).

### The study model

`HarmonicModelOptions.Study` is the model that the CLI `harmonics` verb, the Web HARMONICS tab and the scheduled
`harmonics` analysis job run: machine source impedance on, loads as damping shunts on, balanced sequences on, default
orders, no skin effect, no long-line correction. Machines ground the network and set the resonances; the loads are the
main resistive damping.

### Singular networks

`Y(h)` is singular when part of the network has no path to ground at that order: a radial feeder or island with no
line charging, shunt, machine or load of its own and no connection to a grounded bus. A feeder fed from a bus that a
machine grounds is not singular. There is then no unique voltage for an injected current. Every driver reports it as
`SingularHarmonicNetworkException`, naming the study and the order, and never substitutes a zero response. The CLI
prints `harmonics: FAILED - <message>` to standard error and exits 1, with no THD and no verdict (the capture is
`docs/_captures/harmonics/case-singular.txt`). A penetration run throws for the lowest
singular order among the orders that carry injected current, whatever the order of execution.

## Algorithm

### The complex-solver seam

Harmonic penetration is a complex linear solve, so the subsystem has its own interface, `IComplexLinearSolver`
(`src/GridSim.Core/Abstractions/IComplexLinearSolver.cs`), parallel to the real `ISparseLinearSolver` of the power flow
([ch. 4, Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>)). Its members are `Name`,
`Solve(ComplexCsr a, Complex[] b)` (null when the matrix is singular or the method breaks down) and
`Factor(ComplexCsr a)`, which returns an `IComplexLinearFactorization` for repeated right-hand sides against one matrix.

| Implementation | Method | Role |
|---|---|---|
| `DenseComplexLuSolver` | Dense complex LU with partial pivoting; singular when a pivot is at or below `NumericTolerances.RelativePivotFloor` (1e-12) times the largest `|A_ij|`; null on singular. | The oracle: exact reference for small cases. |
| `RealExpandedComplexSolver` | The real `2n x 2n` expansion below, solved by the real `SparseLu` (Gilbert-Peierls with an RCM fill-reducing ordering). | The default. Scales to `gb-full` (3539 buses). |
| `SparseComplexBiCgStabSolver` | Native complex Jacobi-preconditioned BiCGSTAB with Hermitian inner products; relative residual tolerance `1e-12`, iteration cap `4n + 200`; null on breakdown or stagnation. | A third independent path that cross-validates the other two. |

The real expansion of `(G + jB)(Vr + jVi) = (Ir + jIi)` is

```
[ G  -B ] [ Vr ]   [ Ir ]
[ B   G ] [ Vi ] = [ Ii ]
```

The ordering of the `2n` pattern is cached per solver instance and reused across the orders of a scan, which share one
topology. Every driver defaults to `RealExpandedComplexSolver`. A caller can pass another `IComplexLinearSolver` to
penetration, the scan, the coupled flow and the estimator; the sweep always uses the real-expanded solver, and a
caller-supplied solver runs sequentially.

### Linear penetration

`HarmonicPenetration.Solve(model, sources, options, solver)` runs, for each order `h` in `options.Orders`:

```
inj[b]   = sum over sources at bus b of I_src(h)
if h == 1 or inj is all zero:   V(h) = 0 at every bus, no Y(h) is built
else:                           V(h) = solve( Y(h), inj )
```

An order with no injected current has exactly zero voltage. The result is `HarmonicPenetrationResult(Orders, BusIds,
Voltage[orderIndex][busIndex])`. The solve is linear and superposition-based: a linear network does not couple orders.
Source components whose order is not in `options.Orders` are not read.

With the default solver and more than one order the orders run in parallel (`Parallel.For`, one
`RealExpandedComplexSolver` per worker through `ThreadLocal`) and write by index. A caller-supplied solver runs the
orders sequentially.

### Impedance scan

`HarmonicImpedanceScan.DrivingPoint(model, busId, options, solver)` computes, at bus `k`, for each order in the
options (triplens removed in balanced mode):

```
solve  Y(h) x = e_k                    e_k is a unit current injected at bus k
Z(h)   = x[k]                          the driving-point impedance, pu on BaseMva
```

The solution `x` is column `k` of `Y(h)^-1`. The peak is the order with the largest `|Z(h)|`, with ties broken to the
first (lowest) order, after the parallel solves have finished. The result holds `ResonanceOrder`, `PeakMagnitude` and
`Participation`: the full column `x` at the peak order, the transfer impedance from bus `k` to every bus. An unknown
bus throws `ArgumentException`; a singular order throws for the lowest such order.

### Fine frequency sweep

The scan samples only whole orders, so a sharp resonance between two orders is under-reported at the nearest order.
`HarmonicFrequencySweep.DrivingPoint(model, busId, options, sweep)` sweeps a grid of fractional orders and refines the
peak. The grid settings are `FrequencySweepOptions`.

```
points   = floor( (MaxOrder - MinOrder) / Step + 1e-9 ) + 1
grid[p]  = MinOrder + p * Step                     computed, never accumulated
Zpos[p]  = [ Y_pos(grid[p])^-1 ]_kk                positive-sequence network (BuildSequence, s = +1)
Zneg[p]  = [ Y_neg(grid[p])^-1 ]_kk                swept only when some in-service branch has PhaseShiftDeg != 0
```

The negative-sequence network differs from the positive one only through transformer phase shifts, so without a phase
shifter `NegativeZ` is null. The grid peak is the first (lowest) maximum of `|Zpos|`; the negative network replaces it
only if a point is strictly larger. With the default settings the grid has 486 points.

The refinement is a golden-section search for the maximum of `|Z(h)|` on `[lo, hi]`, the neighbouring grid points of the
grid peak (clamped at the ends of the grid), on the sequence network that holds the peak:

```
invPhi = (sqrt(5) - 1) / 2
steps  = ceil( ln(RefineTolerance / (hi - lo)) / ln(invPhi) )
c = hi - invPhi*(hi - lo);  d = lo + invPhi*(hi - lo)
repeat `steps` times: keep [lo, d] when |Z(c)| >= |Z(d)|, else [c, hi]; add one interior point
result = the best (h, |Z|) seen, never worse than the grid peak
```

The refinement runs sequentially for a fixed step count. With `hi - lo = 0.2` and a tolerance of `1e-6` it takes 26
steps (28 evaluations of `|Z|`). `PeakHz = PeakOrder * FundamentalHz`. The sweep refuses (with `ArgumentException`)
non-positive or non-finite bounds, step or tolerance, `MaxOrder` not above `MinOrder`, a grid of fewer than 2 or more
than `MaxGridPoints` (100,000) points, and an unknown bus. A singular grid point throws, lowest frequency first.

### Coupled iterative harmonic power flow

Linear penetration treats a source as a fixed current. `CoupledHarmonicPowerFlow.Solve(model, sources, coupling,
options, solver, tol, maxIter)` lets a converter's emission depend on its terminal voltage through an
`IHarmonicCouplingModel`, which has two parts: a linear shunt admittance `ShuntAdmittance(order)` (the converter's Norton
admittance) and an `Injection(order, terminalVoltage, baseInjection)` that may depend nonlinearly on the voltage.

For each order `h != 1` that carries injected current:

```
Yc(h)   = Y(h) + ShuntAdmittance(h) at every source bus      (skipped when the admittance is exactly 0)
factor Yc(h) once
V0      = solve( Yc, Injection(h, 0, base) )
loop up to maxIter times:
    V'  = solve( Yc, Injection(h, V[b], base[b]) at each source bus b )
    d   = max over buses of |V'[i] - V[i]|
    V   = V'
    stop when d < tol
Iterations = the largest zero-based index of the converging pass over the orders
Converged  = false when any order used all maxIter passes
```

`tol` defaults to `1e-10` and `maxIter` to 50. Factoring once and back-substituting per pass matches a re-factoring
solve bit for bit. The same triplen refusal as penetration applies. A singular `Yc(h)` throws.

`NortonCouplingModel(couplingConductance = 0.05, couplingSusceptancePerOrder = 0)` is the only implementation:
`ShuntAdmittance(h) = g + j * b * h` and `Injection = baseInjection`, so the iteration settles on the first pass. Its
provenance is `CuratedPlaceholder`: the admittance and the voltage-to-emission law describe a converter's internal
control, which the grid model does not contain. The network physics folding it in is grounded; the converter model is
the assumption and is labelled. The coupled flow is Core API with no CLI verb and is marked experimental.

### Three-phase harmonic penetration

`ThreePhaseHarmonicPenetration.Solve(MultiPhaseModel model, sources, orders, solver, activeFrontEnds)` runs the penetration on the
multi-phase feeder model (`Distribution/`), which carries a full `3 x 3` series impedance per branch, instead of the
positive-sequence equivalent. Buses flagged `IsSource` are the harmonic ground (`V = 0`); every other bus carries three
unknowns, so the reduced system has `3m` rows for `m` non-source buses. For each order `h` in the caller's list:

```
Z_br(h)[p][q] = R[p][q] + j * h * X[p][q]          every element of the 3x3, self and mutual
Ys_br(h)      = Z_br(h)^-1                         3x3 series admittance
branch between non-source buses f, t:  block (f,f) += Ys, (t,t) += Ys, (f,t) -= Ys, (t,f) -= Ys
branch to a source bus:                the non-source end gets block += Ys on its diagonal only
shunt at node u (capacitor, line charging): block (u,u) += G + j*h*B, per entry of its 3x3 fundamental admittance
active front end at node u:            y = 1 / (R + j*h*X);  block (u,u) += (y/3) [[2,-1,-1],[-1,2,-1],[-1,-1,2]]
solve Y(h) V(h) = I(h)                             I(h) = the per-phase injections of ThreePhaseHarmonicSource
```

A branch with both ends at source buses is skipped. Shunt capacitors (`j h B` per phase) and line charging (half the
pi shunt at each end, its susceptance scaled by `h`) are stamped on the diagonal block of their node. A closed switch
(a branch with zero series impedance) has no admittance: union-find over the closed switches makes its two buses one
node with one set of unknowns, and a bus switched onto the source bus is part of the harmonic reference; a harmonic
source on such a bus is refused rather than lost. An active-front-end converter, when `activeFrontEnds` names one,
is a balanced three-wire shunt (each phase reactor `R + j h X` on the feeder base to a floating star point), so it takes
positive- and negative-sequence current and no zero-sequence current; null or empty stamps nothing. Loads of every kind
(constant-power wye, ZIP and delta) are accepted and left out of `Y(h)`: the study is network only. There is no machine
or transformer-shift term, and no triplen refusal: a triplen is a legitimate zero-sequence quantity here. `ThreePhaseHarmonicSource.Balanced`
builds a balanced injection with the harmonic rotation

```
a    = (magnitudePercent / 100) * I1 * exp(j * phaseDeg * pi / 180)
rot  = exp( -j * h * 2*pi/3 )
phases (A, B, C) = ( a,  a * rot,  a * conj(rot) )
```

so phase B lags A by `h * 120` degrees. `ThreePhasePenetrationResult.Sequence(orderIndex, busIndex)` applies the Fortescue
transform and returns `[zero, positive, negative]`
(`SymmetricalComponents.ToSequence`: `V0 = (Va + Vb + Vc)/3`, `V1 = (Va + a*Vb + a^2*Vc)/3`, `V2 = (Va + a^2*Vb + a*Vc)/3`,
`a = exp(j*2*pi/3)`). On a balanced network a balanced triplen is pure zero sequence and a balanced 5th is pure negative
sequence; on an unbalanced network the orders mix sequences, and only this solve gives the neutral and per-phase
distortion. A `MultiPhaseModel` loads from a `gridsim-dist3ph/1` file (see
[ch. 18, the `gridsim-dist3ph/1` feeder file](<0018 Real-time, distribution and dynamic estimation.md>)). The three-phase penetration is
experimental; it is on the CLI as `gridsim dist3ph <feeder> harmonics --bus ID --spectrum
TYPE --current PU` (one balanced library source; manual ch. 7). It refuses, by name, a feeder with an element it does
not model: a single- or two-phase lateral, a step-voltage regulator, or a transformer of any connection.

### Harmonic state estimation

`HarmonicStateEstimator.Estimate(model, order, measurements, candidateSourceBuses, options, solver)` recovers the source
injections that best explain a set of metered harmonic voltages. At a fixed order the network is linear, so the estimate
is one complex weighted least-squares solve. With `s` candidate source buses and `m` measurements
(`HarmonicVoltageMeasurement(Bus, Voltage, Sigma = 1e-3)`):

```
Y(h) zcol_j = e_(source bus j)         one factorisation of Y(h), s back-substitutions
H[i][j]     = zcol_j[ bus of measurement i ]               the m x s measurement matrix
w[i]        = 1 / sigma_i^2
(H^H W H) x = H^H W z                  the Gram system, solved by the dense complex LU on its honest path
residual    = sqrt( sum_i w[i] * |z[i] - sum_j H[i][j] x[j]|^2 )
```

It throws `ArgumentException` when `m < s`, `SingularHarmonicNetworkException` for a singular `Y(h)`, and
`InvalidOperationException` when the Gram matrix is singular to working precision (candidate sources the measurements
cannot distinguish). It never returns a zeroed source. The options default to the bare model
(`new HarmonicModelOptions()`), not `Study`; pass `Study` to estimate on the study network. The estimator is Core API
with no CLI verb and is marked experimental.

### Indices

`HarmonicIndices` reduces a penetration result to the standard scalars. With `V_h` the complex harmonic voltage at a
bus and `V_1` the fundamental magnitude (1.0 pu unless the caller supplies solved fundamental voltages; the CLI and the
Web tab both supply them through `HarmonicIndices.SolvedFundamentalVm`, which matches the power-flow result to the
study's buses by id and throws `InvalidOperationException` on a non-converged fundamental or a missing or non-positive
magnitude):

```
THD_V   = sqrt( sum over h > 1 of |V_h|^2 ) / V_1 * 100             percent
IHD_V(h)= |V_h| / V_1 * 100                                         percent
K       = sum_h ( I_h^2 * h^2 ) / sum_h I_h^2                       over a source spectrum, fundamental included
sharp   = PeakMagnitude / median over the scan of |Z(h)|            median of an even count is the mean of the middle two
```

The THD sum runs over the order index in ascending order, so its rounding is fixed. A worst-THD bus is the first bus in
model order with the strict maximum; with all THD zero it is the first bus. The K-factor is the UL transformer
de-rating weight: a pure fundamental gives `K = 1`. The library 6-pulse spectrum (`I_h = I_1/h` at 5, 7, 11, 13, 17,
19, 23, 25) gives `K = 9 / 1.0843104 = 8.3002`, and a source-current THD of 29.04 %. `sharp = 1` is a flat response;
there is no pass or fail threshold. `IndividualDistortionPct` returns 0 for an order that is not in the result.

### Damping sharpness and the synchronous fleet

Synchronous machines are the network's harmonic sink: the study model stamps each as a reactance to ground, so each is a
stiff path that absorbs harmonic current and sets where the network resonates. Removing a machine removes its path to
ground from `Y(h)`. How the scan peak and `DampingSharpness` change depends on the network (a removed shunt can move a
resonance as well as raise it), and no test pins the direction; compare the two runs. This is the harmonic counterpart
of the inertia in [ch. 5, Frequency dynamics](<0005 Frequency dynamics.md>): the same fleet retirement that erodes
inertia removes the machine paths to ground. A machine itself adds no damping, because machines carry no resistance;
resistive damping comes from the series `R` of the branches, the bus conductance `Gs` and the load conductance.

### Compliance

`HarmonicCompliance.AssessIeee519(model, penetration, fundamentalVm)` gives, for each bus, `ThdPct`, the worst single
order `WorstIhdPct` and its order, the two limits, and `Compliant`, which holds only when the voltage level is known
and both `ThdPct <= ThdLimitPct` and `WorstIhdPct <= IhdLimitPct`. The limit tier comes from the bus's `BaseKv`
(upper bounds inclusive).

IEEE 519-2014 bus-voltage limits (`HarmonicCompliance.Ieee519VoltageLimits`):

| Nominal voltage | THD limit | Single-harmonic limit |
|---|---|---|
| `BaseKv <= 1` | 8.0 % | 5.0 % |
| `1 < BaseKv <= 69` | 5.0 % | 3.0 % |
| `69 < BaseKv <= 161` | 2.5 % | 1.5 % |
| `BaseKv > 161` | 1.5 % | 1.0 % |

GB headline THD planning levels (`G55ThdPlanningLevelPct`), from ENA Engineering Recommendation G5 Issue 5 (2020),
section 5.2, Table 1 "THDV planning levels at supply system PCC nominal operating voltage", p.21 (`docs/reference` S139):

| Nominal voltage | THD planning level |
|---|---|
| `BaseKv <= 0.4` | 5.0 % |
| `0.4 < BaseKv <= 25` | 4.5 % |
| `25 < BaseKv <= 66` | 3.7 % |
| `66 < BaseKv <= 230` | 3.0 % |
| `BaseKv > 230` | 3.0 % |

`G55Verdict(bus)` returns the planning level and whether the bus is within it. The per-order G5 tables (Tables 2 to 6)
are not encoded.

A bus whose `BaseKv` is missing, zero, negative or non-finite has `VoltageLevelKnown = false`. It is judged at the
strictest tier (IEEE 519 1.5 % and 1.0 %, G5 3.0 %) and is never certified, whatever its distortion. The CLI prints a
warning with the count.

### Source library

`HarmonicSourceLibrary` supplies assumed-typical spectra, each tagged `CuratedPlaceholder` and marked `[VERIFY]` in the source comments. Magnitudes are
percent of the fundamental current.

| Type | Orders and magnitudes (%) |
|---|---|
| `6-pulse` | `100/h` at 5, 7, 11, 13, 17, 19, 23, 25 (20, 14.286, 9.091, 7.692, 5.882, 5.263, 4.348, 4.000) |
| `12-pulse` | `100/h` at 11, 13, 23, 25, 35, 37 |
| `pv-inverter` | 3: 2.0; 5: 4.0; 7: 3.0; 11: 2.0; 13: 1.5 |
| `ev-charger` | 3: 12.0; 5: 8.0; 7: 5.0; 9: 3.0; 11: 2.0 |
| `heat-pump-inverter` | 3: 12.0; 5: 9.0; 7: 6.0; 9: 4.0; 11: 3.0; 13: 2.0 |
| `arc-furnace` | 2: 5.0; 3: 8.0; 4: 3.0; 5: 6.0; 7: 4.0 |
| `saturated-transformer` | 2: 15.0; 3: 8.0; 4: 4.0; 5: 3.0 |
| `6-pulse-g5` | 5: 31.4; 7: 10.9; 11: 7.0; 13: 3.9; 17: 2.7; 19: 1.9; 23: 1.0; 25: 1.0; 29: 0.6; 31: 0.7; 35: 0.6; 37: 0.5; 41: 0.4; 43: 0.4; 47: 0.3; 49: 0.2 |
| `12-pulse-g5` | 5: 0.4; 7: 0.2; 11: 4.8; 13: 3.2; 17: 0.1; 23: 1.0; 25: 0.8; 35: 0.4; 37: 0.4; 47: 0.2 |
| `afe-g5` | 2: 0.4; 3: 0.5; 4: 0.2; 5: 3.4; 6: 0.2; 7: 1.5; 8, 9, 10: 0.1; 11: 0.4; 12: 0.1; 13: 0.4; 15, 17: 0.1; 19: 0.2; 21, 22: 0.1; 23, 25: 0.2; 26, 27, 28, 29, 31, 35, 37: 0.1 |
| `ideal` | the same spectrum as `6-pulse` |

The three `-g5` profiles are G5 Issue 5 Annex B, Table B.3 "Harmonic current emission assumptions for three-phase
equipment types", pp.99-100: `Ih / I1` for orders 2 to 50 as percent, orders printed as 0.000 omitted. Their THDI
reproduce the printed 0.3441 (six-pulse) and 0.0596 (twelve-pulse); the active-front-end entries give 0.0386 against the
printed 0.0381, the difference being the rounding of the many 0.001 entries. They are typical profiles, not a measured
converter, so they carry `CuratedPlaceholder` like the rest of the library.

`HarmonicSourceLibrary.Make(type, bus, fundamentalCurrentPu)` builds a source (an unknown type throws). The default
synthetic source, used by the CLI `harmonics` verb without `--spectra` or `--converter-sources`, by `experimental
harmonic-kcl`, by the cycle's harmonics job and by the web HARMONICS tab, is
`Make(HarmonicSourceLibrary.DefaultSyntheticSourceType, largest-Pd bus, 0.5)`, the `6-pulse-g5` profile; `harmonics
--synth-spectrum ideal` uses the 1/h spectrum instead (`--synth-spectrum g5` names the default), and the flag is refused
with `--spectra` or `--converter-sources`. Spectra with a triplen entry (`pv-inverter`, `ev-charger`,
`heat-pump-inverter`, `arc-furnace`, `saturated-transformer`, `afe-g5`) are refused by the balanced study model, and even
orders are not in the default order set.

### Per-converter sources (`--converter-sources`)

`ConverterHarmonicSources.FromModel(model, level, operatingPoint)` (`src/GridSim.Core/Harmonics/ConverterHarmonicSources.cs`)
turns every in-service converter-interfaced generator (`Generator.IsConverterInterfaced`) into one harmonic current
source, except power-flow-only devices. Synchronous machines are harmonic sinks, not sources.

- **Profile.** A self-commutated converter (battery, PV, full-converter wind, VSC HVDC) takes `afe-g5`
  (`SelfCommutatedProfile`); a unit with `ConverterFaultCurrentPu` exactly 0, the model's encoding of a line-commutated
  HVDC link, takes `12-pulse-g5` (`LineCommutatedProfile`; that such a station is twelve-pulse is `[VERIFY]`).
- **Level** (`ConverterHarmonicLevel`). `Rated` (the default, the G5 Stage 1C / 2A assessment basis): `I1 = MBase /
  BaseMva` pu. `Dispatched`: `I1 = sqrt(P^2 + Q^2) / BaseMva` pu, with `P` and `Q` the unit's solved output from the
  fundamental power flow (`SolvedGeneration.UnitOutput`); a unit at zero output injects nothing and is listed as not
  modelled. Part-load profiles are `[VERIFY]`: no document on disk gives them.
- **Components.** Each component is the profile's `Ih / I1` times `I1`, at phase 0, so units add linearly, the G5
  Issue 5 s.6.3.3 rule when no diversity is declared.
- **Orders.** The study runs over orders 2 to 50 (`ConverterHarmonicSources.StudyOrders`, `StudyOptions` =
  `HarmonicModelOptions.Study` with those orders). The balanced study cannot carry the triplen (zero-sequence) orders
  of `afe-g5`; they are left out and each is named in `ConverterHarmonicSourceSet.OmittedComponents`.
- **Fail loud.** A non-finite or non-positive base, a converter rating that is not finite and positive (`Rated`), or a
  non-finite `Pg` / `Qg` (`Dispatched`) throws `ArgumentException`. The CLI exits 1 on a case with no converter that
  injects, 3 when `dispatched` needs a fundamental solve that did not converge, and 2 when `--converter-sources` is
  combined with `--spectra` or `--synth-sources` or given another value than `rated` or `dispatched`.

With `--afe-reactance X [--afe-resistance R]` (`HarmonicModelOptions.ActiveFrontEndReactancePu`,
`ActiveFrontEndResistancePu`, pu on each unit's rating) every active-front-end unit is stamped as a Norton equivalent,
`R + j h X` to ground in parallel with its source (G5 Issue 5 s.3.29: such converters may be resonant plant);
line-commutated units are unchanged. No value exists on disk, so there is no default; without the flag the converter
is an ideal current source. The export then carries a `harmonicModel.activeFrontEnd` block listing each stamped unit
and its impedance on the system base.

### Source set file: `gridsim-harmonics/1`

`HarmonicSourceSet.Load(path)` reads camelCase JSON, property names case-insensitive, enums by name, with fields `schema`,
`case`, `baseMva` (default 100), `takenUtc`, `note` and `sources`. It rejects a `schema` other than `gridsim-harmonics/1`
and, when `takenUtc` is present, calls `ForwardInferenceGuard.RejectFuture`: a stamp newer than `utcNow - 169 h`
(`ForwardInferenceGuard.HorizonHours`) throws `ForwardInferenceException`
([ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>)). `Validate(model)` collects every
offender into one `InvalidDataException`: a `baseMva` that differs from the model's by more than 1e-9 (checked when both are above 0), a source bus
absent from the model, a non-finite fundamental current, a component order below 1, a non-finite magnitude or phase.
The authoritative schema for GDA-written files is `NetworkModel/HarmonicDataSchema.json` in the GDA repository.

### Export: `gridsim-harmonic-export/1`

`HarmonicExport.WriteJson` writes one document with the blocks `schema`, `meta`, `provenance`, `model`, `orders`,
`harmonicModel`, `sources`, `voltage` (non-zero phasors only: `order`, `bus`, `vmPu`, `vaDeg`), `indices`, `resonance`,
`resonanceSweep` and `compliance`; the fields are listed in
[ch. 17](<../manual/0017 Harmonic studies.md>). The `harmonicModel` block records
every input beyond the case network that `Y(h)` used (the machine `xSystemPu` values and the load `pdMw`, `qdMvar`),
so `tools/crossval/crossval_harmonics.py` can rebuild `Y(h)` and the voltages with NumPy alone and diff them. The export
alone is not enough: it holds no branch `R`, `X`, `B`, taps or bus shunts. The script also needs `--case`, a case
directory (`system.json`, `buses.json`, `transmission.json`, optionally `transformer.json`; a built-in case name is not
accepted), and `--sources`, a `gridsim-harmonics/1` file (written from the export's `sources` block after a default run).

### Solver wiring in the production callers

- CLI `harmonics`: `HarmonicModelOptions.Study` (with `--converter-sources`, `ConverterHarmonicSources.StudyOptions`,
  orders 2 to 50); `HarmonicPenetration.ComponentsOutsideOrders` (refuses, exit 1, any non-zero source component at an
  order outside `Orders`; a Core caller uses `HarmonicPenetration.RefuseComponentsOutsideOrders`, and every
  `HarmonicPenetrationResult` carries `ComponentsOutsideOrders`); `HarmonicPenetration`, `HarmonicIndices.FromPenetration`
  normalised by `HarmonicIndices.SolvedFundamentalVm` over the case's own solve (the `--once` solve; exit 3 when it does
  not converge), `HarmonicCompliance.AssessIeee519` with the same voltages, `HarmonicImpedanceScan` and
  `HarmonicFrequencySweep` at the scan bus. A triplen source (`ArgumentException`) exits 1 and an unknown `--bus` exits 2.
- Web HARMONICS tab (`SimSession.StudyHarmonics`): the same default source and `Study` model; THD is normalised by the
  live frame's solved fundamental through the same `HarmonicIndices.SolvedFundamentalVm` (a non-converged frame is a
  `Failure` verdict); it adds the G5 planning-level verdict; it skips a case above 1,500 buses
  (`PresentationLimits.HeavyModelBusLimit`).
- Scheduled `harmonics` job (`AnalysisJobLibrary.Harmonics`): penetration only, on the same source and model, gated at
  `CycleOptions.HarmonicsBusLimit` = 1500 buses.
- `gridsim experimental harmonic-kcl` audits `Y(h) V(h) = I(h)` on the study penetration (physics law 12);
  `gridsim experimental passivity` checks the driving-point impedance is positive-real over 5 to 2500 Hz in 5 Hz steps.

## Parameters and defaults

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Order set | 3, 5, ... 49 (24 orders) | order | Orders solved by penetration and the scan. | `HarmonicModelOptions.DefaultOrders` (`HarmonicModelOptions.cs`) |
| Skin coefficient, overhead and unknown | 0 | none | `k` in `rFactor`; 0 means no skin effect. | `HarmonicModelOptions.SkinCoefficient` |
| Skin coefficient, cable | 0 (falls back to OHL value when 0) | none | `k` for `Equipment == "cable"`. | `HarmonicModelOptions.CableSkinCoefficient` |
| Long-line correction | off | flag | Hyperbolic correction per branch with `B != 0`. | `HarmonicModelOptions.LongLineCorrection` |
| Load shunts | off (on in `Study`) | flag | Loads as damping shunts. | `HarmonicModelOptions.IncludeLoadShunts` |
| Machine sources | off (on in `Study`) | flag | Synchronous machines as `1/(j h X)` to ground. | `HarmonicModelOptions.IncludeGeneratorSources` |
| Balanced sequences | off (on in `Study`) | flag | Sequence-correct phase shift; triplens refused. | `HarmonicModelOptions.BalancedHarmonicSequences` |
| Fallback machine X''d | 0.20 | pu on MBase | Used when a machine carries no X''d. | `HarmonicModelOptions.GeneratorFallbackXdPu` (`FaultAnalysis.DefaultSubtransientXdPu`) |
| Sweep lowest order | 1.5 | order | First grid point (75 Hz at 50 Hz). | `FrequencySweepOptions.MinOrder` |
| Sweep highest order | 50.0 | order | Last grid point (2.5 kHz at 50 Hz). | `FrequencySweepOptions.MaxOrder` |
| Sweep step | 0.1 | order | Grid spacing (5 Hz at 50 Hz). | `FrequencySweepOptions.Step` |
| Refinement tolerance | 1e-6 | order | Final golden-section bracket width. | `FrequencySweepOptions.RefineTolerance` |
| Grid cap | 100,000 | points | Largest accepted sweep grid. | `FrequencySweepOptions.MaxGridPoints` |
| CLI source current | 0.5 | pu | Fundamental current of the default synthetic source (`6-pulse-g5`). | `CliDefaults.DefaultHarmonicSourcePu` (`GridSim.Cli/CliDefaults.cs`) |
| Coupled tolerance | 1e-10 | pu | Largest voltage change that stops the fixed point. | `CoupledHarmonicPowerFlow.Solve` parameter `tol` |
| Coupled iteration cap | 50 | passes | Passes before `Converged = false`. | `CoupledHarmonicPowerFlow.Solve` parameter `maxIter` |
| Norton conductance | 0.05 | pu | Default converter shunt conductance. | `NortonCouplingModel` constructor |
| Estimator sigma | 1e-3 | pu | Default measurement standard deviation. | `HarmonicVoltageMeasurement.Sigma` |
| Dense pivot floor | 1e-12 times max `|A_ij|` | relative | Singular threshold of the dense oracle. | `NumericTolerances.RelativePivotFloor` |
| BiCGSTAB tolerance | 1e-12 | relative | Complex BiCGSTAB residual tolerance. | `NumericTolerances.DefaultComplexKrylovTolerance` |
| Forward-inference horizon | 169 | hours | Newest permitted `takenUtc` lag. | `ForwardInferenceGuard.HorizonHours` |
| Heavy-model gate | 1500 | buses | Web tab and scheduled job skip larger cases. | `PresentationLimits.HeavyModelBusLimit`, `CycleOptions.HarmonicsBusLimit` |

## Determinism

Harmonic results are bit-identical across runs and across execution order:

- Orders run in parallel only when the default solver is in use, each worker has its own solver, and every result is
  written by order index. No reduction crosses threads.
- The scan reduces its peak after the parallel solves finish, sequentially, with a first-maximum tie-break.
- Sweep grid points are `MinOrder + p * Step`, computed from the index and never accumulated. Each point is independent
  and written by index; the grid peak tie-break is the lowest point; the refinement is sequential with a fixed step count.
- The cached RCM ordering is a pure function of the matrix pattern, so reusing it across orders gives the same bits as a
  fresh ordering. Factoring once and back-substituting per right-hand side gives the same bits as one solve per
  right-hand side.
- A singular order is reported for the lowest singular order, whatever order the workers finished in.
- THD, IHD and `Compliant` are sums and comparisons over a fixed index order.

What the tests pin is the same process: `HarmonicFrequencySweepTests.Repeated_sweeps_are_bit_identical` repeats a sweep,
and the `ComplexSolverReusePinTests` compare cached and fresh solves. Across CPU types the mechanism is that the harmonic
path (`RealExpandedComplexSolver` over `SparseLu`, `System.Numerics.Complex` arithmetic) uses scalar double operations
with no `SimdKernels` call and no explicit fused multiply-add. No automated test runs the harmonic path on different CPU
types or compares the dense and real-expanded complex solvers bit for bit (`ComplexSolverCrossValidationTests` uses tolerances of 1e-9 and 1e-8).

## Invariants

| Invariant | Test |
|---|---|
| At `h = 1` with default options `Y(1)` is the fundamental `YBus`, cell for cell. | `FrequencyDependentYBusTests.Y_of_order_one_reduces_to_the_fundamental_ybus`, `ComplexCsrBuilderPinTests.Fundamental_Y_h_build_is_unchanged_by_the_sort_port` |
| Series reactance and line charging scale with `h`. | `FrequencyDependentYBusTests.Series_reactance_and_charging_scale_with_the_harmonic_order` |
| Cable and overhead branches get different skin factors and different `Y(h)`; the long-line correction is the identity on a short line and bites on a long one. | `HarmonicNetworkDataTests.Cable_and_ohl_get_different_skin_factors_and_different_Yh`, `HarmonicNetworkDataTests.Long_line_correction_is_identity_for_a_short_line_and_bites_for_a_long_one` |
| A machine adds exactly `-1/(h X)` to its bus susceptance; `X` is X2 plus the unit transformer on the system base; converter and power-flow-only units are not stamped; a zero reactance fails loud naming the unit. | `HarmonicSourceImpedanceTests.Machine_stamp_adds_exactly_minus_one_over_h_x_to_the_bus_susceptance`, `Machine_source_reactance_is_x2_plus_unit_transformer_on_the_system_base`, `Converter_and_power_flow_only_units_are_not_stamped`, `A_zero_source_reactance_fails_loudly_naming_the_unit` |
| The study preset turns on machines and loads and nothing else; default options add no machine terms. | `HarmonicSourceImpedanceTests.Study_preset_turns_on_machines_and_loads_and_nothing_else`, `Default_options_add_no_machine_terms`, `HarmonicBalancedSequenceTests.Study_preset_is_balanced` |
| A net-exporting bus adds no conductance; a leading load is a capacitor whose susceptance grows with `h`; a lagging load is a reactor that falls as `1/h`. | `HarmonicBalancedSequenceTests.A_net_exporting_bus_adds_no_conductance`, `A_leading_load_is_a_capacitor_whose_susceptance_grows_with_order`, `A_lagging_load_is_unchanged_a_reactor_whose_susceptance_falls_with_order` |
| A negative-sequence order sees the phase shift reversed; a positive-sequence order keeps it. | `HarmonicBalancedSequenceTests.A_negative_sequence_order_sees_the_phase_shift_reversed`, `A_positive_sequence_order_keeps_the_phase_shift` |
| A triplen `Y(h)` cannot be built in balanced mode; a non-integer or non-positive order is refused; penetration and the coupled flow refuse a triplen source naming it; triplen orders carry exactly zero voltage; the scan skips them. | `HarmonicBalancedSequenceTests.A_triplen_Y_h_cannot_be_built_in_balanced_mode`, `A_non_integer_or_non_positive_order_is_refused_in_balanced_mode`, `Penetration_refuses_a_triplen_source_in_balanced_mode_naming_it`, `Coupled_flow_refuses_a_triplen_source_in_balanced_mode`, `Study_penetration_with_a_six_pulse_source_gives_exact_zero_at_triplen_orders`, `Study_scan_skips_triplen_orders` |
| Orders rotate positive, negative, zero by `h mod 3`. | `HarmonicAnalysisTests.Balanced_orders_rotate_positive_negative_zero` |
| Penetration satisfies `Y(h) V(h) = I(h)`; KCL balances; the scan equals the response to a unit injection. | `HarmonicAnalysisTests.Penetration_solves_the_network_equations_Yh_V_equals_I`, `Impedance_scan_equals_the_penetration_response_to_a_unit_injection`, `HarmonicPhysicsTests.Harmonic_kcl_holds_for_a_solved_penetration` |
| The scan finds a parallel resonance above the fundamental. | `HarmonicAnalysisTests.The_scan_finds_a_parallel_resonance_above_the_fundamental` |
| The sweep finds a resonance between integer orders at its analytic frequency and the refined magnitude matches the exact value. | `HarmonicFrequencySweepTests.Finds_a_resonance_between_integer_orders_at_its_analytic_frequency`, `Refined_peak_magnitude_matches_the_exact_single_bus_value` |
| Sweeps are bit-identical run to run; grid points are `min + k*step`. | `HarmonicFrequencySweepTests.Repeated_sweeps_are_bit_identical`, `Grid_points_are_min_plus_k_step_never_accumulated` |
| A phase shifter makes the sweep cover the negative-sequence network. | `HarmonicFrequencySweepTests.A_phase_shifter_sweeps_the_negative_sequence_network_too` |
| Invalid sweep settings and an unknown bus are refused; an ungrounded network fails loud at the lowest frequency. | `HarmonicFrequencySweepTests.Invalid_sweep_settings_are_refused`, `Unknown_bus_is_refused`, `An_ungrounded_network_fails_loud_at_the_lowest_frequency` |
| Every harmonic study fails loud on a singular `Y(h)`, naming the lowest order, on the parallel and the sequential path. | `HarmonicSourceImpedanceTests.Penetration_on_an_ungrounded_radial_feeder_throws_naming_the_lowest_order`, `Penetration_singular_order_is_deterministic_on_the_sequential_path_too`, `Impedance_scan_on_an_ungrounded_radial_feeder_throws`, `Coupled_harmonic_flow_on_an_ungrounded_radial_feeder_throws`, `WebHarmonicsStudyFailureTests.Ungrounded_network_study_is_a_visible_failure_with_the_engine_message` |
| The study model solves a radial feeder with real distortion. | `HarmonicSourceImpedanceTests.Study_model_solves_the_radial_feeder_with_real_distortion` |
| The three complex solvers agree and satisfy `A x = b`; all return null on a singular system. | `ComplexSolverCrossValidationTests.All_three_complex_solvers_agree_and_satisfy_Ax_equals_b`, `ComplexSolverSingularContractTests.All_three_solvers_return_null_on_a_singular_system` |
| A cached ordering and factor-once reuse are bit-identical to fresh solves; penetration and scan are unchanged end to end. | `ComplexSolverReusePinTests.Cached_ordering_across_same_pattern_orders_matches_a_fresh_solver_bit_for_bit`, `Factor_once_multi_rhs_matches_solve_per_rhs_bit_for_bit`, `Penetration_is_unchanged_end_to_end`, `Impedance_scan_is_unchanged_end_to_end` |
| THD is the root-sum-square of harmonic voltages over the fundamental; the 6-pulse K-factor is in the 7 to 10 band and a pure fundamental is 1; sharpness is high for a tall resonance. | `HarmonicIndicesTests.Voltage_thd_is_the_rms_of_the_harmonic_voltages_over_the_fundamental`, `Six_pulse_load_has_a_transformer_k_factor_near_nine`, `Damping_sharpness_is_high_for_a_tall_resonance` |
| IEEE 519 limits tighten with voltage level; the same distortion passes at LV and fails at EHV. | `HarmonicIndicesTests.Ieee519_voltage_limits_tighten_with_voltage_level`, `Compliance_passes_at_lv_and_fails_the_same_distortion_at_ehv` |
| An unknown voltage level is judged at the strictest tier and never certified, for IEEE 519 and G5, and the export flags it. | `HarmonicComplianceUnknownKvTests.An_unknown_voltage_level_is_judged_at_the_strictest_tier_and_never_certified`, `Even_negligible_distortion_cannot_be_certified_without_a_voltage_level`, `A_known_voltage_level_is_unchanged`, `G55_verdict_follows_the_same_rule`, `Export_flags_the_unknown_voltage_level` |
| The coupled flow with no coupling equals linear penetration; a damping coupling reduces the resonant voltage and converges; the coupling model is tagged as assumed. | `HarmonicCoupledTests.With_no_coupling_it_matches_linear_penetration`, `A_damping_coupling_model_reduces_the_resonant_voltage_and_converges`, `The_coupling_model_is_honestly_tagged_as_assumed` |
| The estimator recovers one and two known injections, rejects an under-determined problem, refuses indistinguishable sources and never fits against a zero impedance column. | `HarmonicStateEstimatorTests.Recovers_a_single_known_injection_to_machine_precision`, `Recovers_two_simultaneous_sources`, `Rejects_an_under_determined_problem`, `HarmonicStateEstimatorFailLoudTests.Indistinguishable_candidate_sources_are_refused_not_zeroed`, `The_estimator_no_longer_substitutes_a_zero_impedance_column` |
| A balanced triplen is pure zero sequence and a balanced fifth pure negative sequence; an unbalanced injection yields all three sequences. | `ThreePhaseHarmonicsTests.A_balanced_triplen_is_pure_zero_sequence`, `A_balanced_fifth_is_pure_negative_sequence`, `An_unbalanced_injection_produces_all_three_sequences`, `A_balanced_diagonal_feeder_keeps_the_three_phase_magnitudes_equal` |
| A source set round-trips; a wrong schema and a fresh timestamp are refused; a source on an absent bus is flagged. | `HarmonicSourceSetTests.Round_trips_through_json`, `Rejects_a_wrong_schema_version`, `A_fresh_timestamp_is_refused_but_a_historical_one_is_accepted`, `Validate_flags_a_source_on_a_bus_the_model_lacks` |
| The G5 planning levels follow G5 Issue 5 Table 1; the three `-g5` profiles reproduce the printed THDI (the active-front-end one within rounding); the default synthetic source is `6-pulse-g5` and `ideal` is the 1/h spectrum. | `S3HarmonicAndEventGroundingTests.G55_planning_level_follows_G5_Issue_5_Table_1`, `G5_six_pulse_profile_reproduces_the_printed_THDI`, `G5_twelve_pulse_profile_reproduces_the_printed_THDI`, `G5_active_front_end_profile_is_within_the_rounding_of_the_printed_THDI`; `R1DefaultsTests.N131_the_default_synthetic_source_is_the_g5_six_pulse_profile_and_ideal_is_the_1_over_h` |
| Per-converter sources: each component is the G5 table value times `I1` at phase 0; the CLI names the sources and the omitted triplens, exits 1 on a case with no converter and 2 on a usage error | `N036N075ConverterHarmonicSourcesTests.Each_component_is_the_g5_table_value_times_i1_at_phase_zero`, `Cli_converter_sources_on_the_uk_case_names_the_sources_and_the_omissions`, `Cli_converter_sources_on_a_case_with_no_converter_exits_1`, `Cli_converter_sources_usage_errors_exit_2` |
| The 6-pulse library spectrum injects `1/h`; the heat-pump spectrum is low-order odd and scales with the fundamental. | `HarmonicSourceSetTests.The_six_pulse_library_spectrum_injects_one_over_h`, `The_heat_pump_inverter_spectrum_is_low_order_odd_and_scales_with_the_fundamental` |
| The export contract is locked, records the machine reactances and loads, and carries the refined sweep peak. | `HarmonicExportContractTests.Export_locks_the_harmonic_export_contract`, `HarmonicSourceImpedanceTests.Export_records_the_machine_reactances_and_loads_the_study_used`, `HarmonicFrequencySweepTests.Export_records_the_refined_sweep_peak` |
| The `--bus` flag is read through the fail-loud integer parser `FlagInt`. The test checks the source text of `Program.cs` for that call; it does not run the CLI. | `CliFailLoudFlagContractTests.Harmonics_bus_flag_is_parsed_fail_loud` |

## Worked example

The run is `gridsim harmonics case9 --enableExport=docs/_captures/harmonics/case9-export.json`, captured in
`docs/_captures/harmonics/case9-export.json` and `case9-export.txt`. The default synthetic source is the G5 profile
`6-pulse-g5` (5th 31.4 %, 7th 10.9 %, odd non-triplen orders up to 49): one 6-pulse source of 0.5 pu
at bus 9, the largest-demand bus (125 MW). The export block `harmonicModel` records what `Y(h)` used beyond the case:
three machines with `xSystemPu` 0.08080808080808081 (bus 1), 0.10416666666666667 (bus 2) and 0.15625 (bus 3), and loads
at buses 5, 7 and 9 of 90 + j30, 100 + j35 and 125 + j50 (MW, MVAr). Every bus is 345 kV.

**Bus 9 at order 7.** The diagonal terms the study model adds at bus 9 at `h = 7` are, from the export numbers and the
formulas above:

```
load  g = 125 / 100 = 1.25 pu             b = -(50 / 7) / 100 = -0.0714286 pu
```

and the machine at bus 1 contributes `b = -1 / (7 * 0.08080808080808081) = -1.7678571 pu` to bus 1. The injected
current at order 7 is `I7 = 0.5 * 10.9 / 100 = 0.0545 pu` (the exported spectrum entry is
`magnitudePercent` 10.9). The exported voltage at bus 9, order 7 is

```
    {
      "order": 7,
      "bus": 9,
      "vmPu": 0.035837397144042,
      "vaDeg": 4.0772958508525905
    },
```

Because bus 9 is the only injection point, the voltage there is the driving-point impedance times the current:

```
|Z_99(7)| = |V9(7)| / |I7| = 0.035837397144042 / 0.0545 = 0.6575669
```

The exported `resonance` block gives `zMag` 0.6575669200741651 for the whole-number scan at bus 9, which peaks at order 7.
The quotient is 0.6575669200741652, so the two agree to 15 significant digits and differ by 1e-16: the scan solve `Y(7) x = e_9` and the penetration solve `Y(7) V = I` are the same
linear system scaled by `I7`.

**THD at bus 9.** The exported `voltage` entries for bus 9 (the G5 profile injects all 16 non-triplen orders from 5 to
49; orders that are multiples of 3 are exactly zero and are not exported):

| Order `h` | `vmPu` (pu) | `vmPu^2` |
|---|---|---|
| 5 | 0.04860883648700535 | 2.362819e-03 |
| 7 | 0.035837397144042 | 1.284319e-03 |
| 11 | 0.015562805056876931 | 2.422009e-04 |
| 13 | 0.005086517066291241 | 2.587266e-05 |
| 17 | 0.004864959318851337 | 2.366783e-05 |
| 19 | 0.0026758684535858356 | 7.160272e-06 |
| 23 | 0.0010449408875055702 | 1.091901e-06 |
| 25 | 0.0009351290269347221 | 8.744663e-07 |
| 29 | 0.0004670035952596365 | 2.180924e-07 |
| 31 | 0.0005037319283338796 | 2.537459e-07 |
| 35 | 0.0003759888281140457 | 1.413676e-07 |
| 37 | 0.00029453345914916843 | 8.674996e-08 |
| 41 | 0.00021059058577289037 | 4.434839e-08 |
| 43 | 0.00020003715091016292 | 4.001486e-08 |
| 47 | 0.00013642671826800105 | 1.861225e-08 |
| 49 | 8.702365238770053e-05 | 7.573116e-09 |

```
sum of vmPu^2        = 3.948816549e-03       (from the unrounded vmPu values)
sqrt(sum)            = 0.06283960971
V_1(bus 9)           = 0.9956308580 pu       (the solved fundamental voltage of bus 9)
THD_V(bus 9)         = 0.06283960971 / 0.9956308580 * 100 = 6.311536972 %
```

The exported `indices` block gives `worstThdPct` 6.311536972429553 at `worstBusId` 9, and the console prints `6.31%`. The
two agree.

**Compliance at bus 9.** The largest single harmonic is the 5th: `IHD = 0.04860883648700535 / 0.9956308580 * 100 = 4.8822 %`.
The bus is 345 kV, above 161 kV, so the limits are 1.5 % THD and 1.0 % single harmonic. The export entry is

```
    {
      "bus": 9,
      "baseKv": 345,
      "thdPct": 6.311536972429553,
      "thdLimitPct": 1.5,
      "worstIhdPct": 4.882214737929257,
      "worstIhdOrder": 5,
      "ihdLimitPct": 1,
      "compliant": false,
      "voltageLevelKnown": true
    }
```

Both tests fail (6.3115 > 1.5 and 4.8822 > 1.0). The console count is `0/9 buses compliant`.

**Sweep.** The sweep grid has `floor((50 - 1.5)/0.1 + 1e-9) + 1 = 486` points, matching `gridPoints`. The grid peak
is bracketed by its two neighbours, 0.2 orders wide, and the golden-section search narrows it to 1e-6 order. It lands at
`peakOrder` 7.47497016807171 (`peakHz` 448.4982100843026, which is `7.47497016807171 * 60`, the case's 60 Hz) with `zMag` 0.687623565377445,
above the whole-number peak 0.6575669 at order 7, as the refinement guarantees.

**Sharpness.** The whole-number scan covers the 16 non-triplen orders; `sharpness` 3.83763679770113 is
`0.6575669 / median(|Z|)`, so the median of the 16 magnitudes is `0.6575669 / 3.83763679770113 = 0.171347`.

The captured sources are in `docs/_captures/harmonics/`; `gb-full.txt` shows the same lines on the 3539-bus model, where
the sweep peak at `h=4.9818` (157.445 pu) is 40.5 % above the whole-number peak at order 5 (112.064 pu).

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/Harmonics/HarmonicModelOptions.cs` | `HarmonicModelOptions` | Order set, skin, long-line, load-shunt, machine-source and balanced-sequence switches; the `Study` preset; `SkinFactor`. |
| `src/GridSim.Core/Harmonics/FrequencyDependentYBus.cs` | `FrequencyDependentYBus` | `Y(h)` assembly: branch, shunt, load, machine and long-line terms; `BuildSequence`; `BalancedShiftSign`; machine reactance. |
| `src/GridSim.Core/PowerFlow/AdmittanceStamp.cs` | `AdmittanceStamp` | The shared pi-model stamp with tap, phase shift, `h` and `rFactor`. |
| `src/GridSim.Core/Harmonics/HarmonicSource.cs` | `HarmonicSource`, `HarmonicComponent`, `HarmonicSourceLibrary` | Source spectra, `InjectionAt`, and the assumed-typical library. |
| `src/GridSim.Core/Harmonics/HarmonicSequence.cs` | `HarmonicSequence`, `HarmonicSequenceKind` | Positive, negative and zero sequence by `h mod 3`. |
| `src/GridSim.Core/Harmonics/HarmonicPenetration.cs` | `HarmonicPenetration`, `HarmonicPenetrationResult` | Linear penetration solve and the triplen refusal. |
| `src/GridSim.Core/Harmonics/HarmonicImpedanceScan.cs` | `HarmonicImpedanceScan`, `ImpedanceScanResult` | Driving-point impedance scan at whole orders. |
| `src/GridSim.Core/Harmonics/HarmonicFrequencySweep.cs` | `HarmonicFrequencySweep`, `FrequencySweepOptions`, `FrequencySweepResult` | Fine sweep and golden-section refinement. |
| `src/GridSim.Core/Harmonics/HarmonicIndices.cs` | `HarmonicIndices` | THD, IHD, K-factor, damping sharpness. |
| `src/GridSim.Core/Harmonics/HarmonicCompliance.cs` | `HarmonicCompliance`, `BusComplianceResult` | IEEE 519 limits and the G5 Issue 5 planning levels and verdicts. |
| `src/GridSim.Core/Harmonics/CoupledHarmonicPowerFlow.cs` | `CoupledHarmonicPowerFlow`, `IHarmonicCouplingModel`, `NortonCouplingModel` | Coupled iterative harmonic flow. |
| `src/GridSim.Core/Harmonics/ThreePhaseHarmonics.cs` | `ThreePhaseHarmonicPenetration`, `ThreePhaseHarmonicSource` | Three-phase penetration and the sequence view. |
| `src/GridSim.Core/Harmonics/HarmonicStateEstimator.cs` | `HarmonicStateEstimator` | Complex weighted least-squares source estimation. |
| `src/GridSim.Core/Harmonics/SingularHarmonicNetworkException.cs` | `SingularHarmonicNetworkException` | The fail-loud error for a singular `Y(h)`. |
| `src/GridSim.Core/Abstractions/IComplexLinearSolver.cs` | `IComplexLinearSolver`, `IComplexLinearFactorization` | The complex-solver seam. |
| `src/GridSim.Core/Numerics/ComplexLinearSolvers.cs` | `DenseComplexLuSolver`, `RealExpandedComplexSolver`, `SparseComplexBiCgStabSolver` | The three solver implementations. |
| `src/GridSim.Core/IO/HarmonicSourceSet.cs` | `HarmonicSourceSet` | `gridsim-harmonics/1` load, validation and the forward-inference guard. |
| `src/GridSim.Core/IO/HarmonicExport.cs` | `HarmonicExport` | `gridsim-harmonic-export/1` writer. |
| `src/GridSim.Cli/Program.cs` | `RunHarmonics` | The `harmonics` verb. |
| `src/GridSim.Cli/CliDefaults.cs` | `CliDefaults.DefaultHarmonicSourcePu` | The default source current. |
| `src/GridSim.Core/Scheduling/AnalysisJobLibrary.cs` | `AnalysisJobLibrary.Harmonics` | The scheduled penetration job. |
| `src/GridSim.Web/Sessions/SimSession.cs` | `StudyHarmonics` | The Web HARMONICS tab study. |
| `tools/crossval/crossval_harmonics.py` | script | Independent NumPy rebuild of `Y(h)` and the voltages from an export plus a case directory and a source file. |

## Limits

- The `harmonics` verb is positive-sequence and balanced, and a source with a non-zero triplen component is refused.
  The three-phase penetration runs as `dist3ph <feeder> harmonics`, with one balanced library source.
- The three-phase penetration has no transformer, load or machine term (laterals, regulators and transformers are
  refused; loads are left out of `Y(h)`), and its sequence content is not validated against an independent
  three-phase reference.
- The coupled harmonic flow has one coupling model, the placeholder `NortonCouplingModel`, no CLI verb and no
  independent coupled-solver validation.
- The harmonic state estimator has no input format for dated harmonic voltage measurements, no CLI verb and no
  independent reference.
- A source spectrum component at an order outside `HarmonicModelOptions.Orders` (for example 2, 4, 53 or 55) is not
  injected by `HarmonicPenetration.Solve`; the result lists it in `ComponentsOutsideOrders`, and the CLI and
  `RefuseComponentsOutsideOrders` refuse it.
- No test pins the IEEE 519 tier boundaries at 1, 69 and 161 kV. No CLI test covers the `harmonics: FAILED - ` path.
- No machine resistance is modelled, so resonance peaks are sharper than a machine with resistance would give.
- Only the headline G5 Issue 5 THD planning levels (Table 1) are encoded, not the per-order tables.
- The per-converter sources use full-load G5 profiles at every level, and the active-front-end internal impedance has
  no sourced value.
- The supraharmonic band (2 to 150 kHz) is not modelled; the sweep ends at h = 50 (2.5 kHz at 50 Hz).


## See also

- [ch. 17, Harmonic studies](<../manual/0017 Harmonic studies.md>) - the `harmonics` verb in practice.
- [ch. 4, Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>) - the real `SparseLu` that `RealExpandedComplexSolver` runs on.
- [ch. 3, Power flow](<0003 Power flow.md>) - the fundamental `YBus` that `Y(1)` reproduces.
- [ch. 5, Frequency dynamics](<0005 Frequency dynamics.md>) - the inertia whose harmonic counterpart is the damping that retiring machines remove.
- [ch. 6, State estimation](<0006 State estimation.md>) - the estimator identity that `HarmonicStateEstimator` carries into the harmonic domain.
- [ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>) - the `ProvenanceTag` taxonomy and the forward-inference horizon.
- [ch. 10, File and wire formats](<0010 File and wire formats.md>) - the versioned-schema conventions of `gridsim-harmonics/1` and `gridsim-harmonic-export/1`.
- [ch. 18, Real-time, distribution and dynamic estimation](<0018 Real-time, distribution and dynamic estimation.md>) - the multi-phase model the three-phase penetration runs on.
- [ch. 19, CLI reference: harmonics](<../manual/0019 CLI reference.md#harmonics>) - flags and exit codes.
