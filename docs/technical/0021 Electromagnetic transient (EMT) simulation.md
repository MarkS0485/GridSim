# 21. Electromagnetic transient (EMT) simulation

This chapter specifies the time-domain electromagnetic transient engine: the Dommel nodal companion-model solver
that steps the instantaneous network (`L di/dt`, `C dv/dt`) at a fixed microsecond timestep, the network builder
that turns a `GridModel` into EMT elements, the `gridsim emt` driver, and the three-phase (abc) engine. The
single-phase engine is `EmtEngine` (`src/GridSim.Core/Dynamics/Emt/EmtEngine.cs`); the builder is
`EmtNetwork.FromGridModel` (`src/GridSim.Core/Dynamics/Emt/EmtNetwork.cs`); the three-phase engine is
`ThreePhaseEmtEngine` (`src/GridSim.Core/Dynamics/Emt/ThreePhaseEmt.cs`).

EMT is the waveform-level counterpart of the other tools: the centre-of-inertia frequency model
([ch. 5](<0005 Frequency dynamics.md>)) works on one system frequency, the harmonic and impedance engines
([ch. 19](<0019 Harmonics.md>)) work in phasors, and EMT resolves the waveform itself. An EMT run is a forward
simulation about the model, not inference about the grid: it sits in `Dynamics/` beside the other forward
simulation tools, does not touch the estimator, and its output is a model trajectory. The design record is
[`design/emt-time-domain.md`](design/emt-time-domain.md); the task-level guide is
[ch. 16 of the user manual](<../manual/0016 Electromagnetic transients (EMT).md>).

The engine is deterministic: the same network, step, rule and events give the same IEEE-754 bit pattern on every
run on a given machine and runtime ([Determinism and bit-identity](#determinism-and-bit-identity) states what is
pinned). The conductance matrix is real, symmetric in value, constant between switching events, factored by sparse
LU and back-substituted once per step (twice at a CDA event step). Every quantity is per-unit on the model's `BaseMva`;
inductance and capacitance are per-unit values in seconds (`L = X/w0`, `C = B/w0` for the total charging, which the
build splits as `(B/2)/w0` at each end) and the step `dt` is in seconds, so the
products `L/dt` and `C/dt` are per-unit conductances. Fields and per-unit conventions are defined in
[ch. 2](<0002 Data model and per-unit conventions.md>).

## Model

### Per-unit values

`w0 = 2*pi*FrequencyHz`, where `FrequencyHz` is the model's own system frequency (`GridModel.FrequencyHz`,
default 50 Hz), never the `--freq` flag: `--freq` sets only the source waveform. `EmtUnits`
(`src/GridSim.Core/Dynamics/Emt/EmtUnits.cs`) derives the element values:

```
series arm            R = br.R                      L = br.X / w0        (X >= 0)
series capacitor      R = br.R                      C = 1 / (|X| * w0)   (X <  0)
line charging, each end   C_end = (br.B / 2) / w0                         (B >  0)
negative charging, each end   L_end = 1 / ((|B| / 2) * w0)               (B <  0)
bus shunt             G = Gs / BaseMva   C = (Bs/BaseMva) / w0   (Bs > 0)
                                         L = 1 / (-(Bs/BaseMva) * w0)  (Bs < 0)
constant-impedance load   G = Pd / BaseMva
                          L = 1 / ((Qd/BaseMva) * w0)    (Qd > 0, inductive)
                          C = ((|Qd|)/BaseMva) / w0      (Qd < 0, capacitive)
Bergeron line         Zc = sqrt(X / B)              tau = sqrt(X * B) / w0
```

The reactance of a series capacitor at `w0` is exactly `X`, and the susceptance of a charging or shunt element at
`w0` is exactly the data's value. A case whose buses carry `BaseKv = 0` runs per-unit only; the engine reports
per-unit voltages and currents and never converts to volts or amperes.

### Elements

`EmtElement` (`src/GridSim.Core/Dynamics/Emt/EmtElement.cs`) is an immutable record addressed by integer node ids;
`EmtNetwork.Ground = -1` is the reference node, which never enters the solve.

| Element | Fields | Meaning |
|---|---|---|
| `SeriesBranch` | `Id, From, To, R, L, Tap = 1` | Series R-L arm; positive current is From to To. `L = 0` is a pure resistor. `Tap` is an ideal `Tap : 1` transformer on the From side. |
| `SeriesCapacitorBranch` | `Id, From, To, R, C, Tap = 1` | Series R-C arm (series compensation) in one companion, no internal node. |
| `ShuntElement` | `Id, Bus, G = 0, C = 0, L = 0` | Up to three parallel elements to ground; each non-zero part becomes its own branch. |
| `VoltageSource` | `Id, Bus, Rs, Emf(t)` | `Rs > 0`: Norton equivalent. `Rs <= 0`: ideal source, the bus becomes a Dirichlet node. |
| `CurrentSource` | `Id, Bus, Inj(t)` | Injects `Inj(t)` into the bus, returned from ground. |
| `SwitchElement` | `Id, From, To, Ron = 1e-6, Roff = 1e9, InitiallyClosed = true` | Conductance toggling between `1/Ron` and `1/Roff`; permanently in the matrix pattern. |
| `BergeronLine` | `Id, From, To, SurgeImpedance, TauSeconds` | Lossless distributed-parameter line. |

### Nodal equation

Unknowns are the voltages of the free nodes: every node except the Dirichlet nodes of ideal sources, which are
eliminated. With `n_free = NodeCount - (number of Dirichlet nodes)` the system each step is

```
G * v_free = i_hist + i_src + i_dir

G       n_free x n_free, real, symmetric in value, built by stamping every branch and line
i_hist  history currents of the reactive branches, and the wave currents of the Bergeron lines
i_src   Norton and current-source injections at time t
i_dir   coupling of each branch to a Dirichlet node: g * v_dir (g * S * v_dir for a tapped arm)
```

A two-terminal branch carries `i = Geq * v_branch + I_hist` from K to M, with `v_branch = v_K - v_M` (or
`S*v_K - v_M` behind a tap, below). Kirchhoff's current law at each free node moves the history term to the
right-hand side with a minus sign for the K end and a plus sign for the M end.

### Companion models

Every reactive branch is reduced to a conductance `Geq` and a history current by one linear form
(`Companion`, `src/GridSim.Core/Dynamics/Emt/Companion.cs`):

```
i(n) = Geq * v(n) + I_hist(n)
I_hist(n) = HistCoeffV * v(n-1) + HistCoeffI * i(n-1)
```

`v` and `i` are the branch voltage and branch current of the previous step; `Companion.History(vPrev, iPrev)`
evaluates the form. The factories are in `EmtCompanionStamp`. `dt` below is the step in seconds; in a
critical-damping burst it is `dt/2` (see "Switching events and CDA").

Resistor and switch (`EmtCompanionStamp.Resistor`; a switch is a resistor whose `Gfixed` the engine toggles):

```
Geq = 1/R          HistCoeffV = 0          HistCoeffI = 0
```

Series R-L (`EmtCompanionStamp.SeriesRl`; `v = R i + L di/dt`). `L <= 0` returns the resistor exactly.

```
trapezoidal:    Geq = 1 / (R + 2L/dt)
                alpha = (2L/dt - R) / (2L/dt + R)
                I_hist = Geq * v_prev + alpha * i_prev
backward Euler: Geq = 1 / (R + L/dt)
                I_hist = i_prev / (1 + R*dt/L)                (HistCoeffV = 0)
```

Shunt inductor (`EmtCompanionStamp.Inductor`) is `SeriesRl` with `R = 0`: trapezoidal `Geq = dt/(2L)`,
`I_hist = Geq*v_prev + i_prev`; backward Euler `Geq = dt/L`, `I_hist = i_prev`.

Capacitor (`EmtCompanionStamp.Capacitor`; `i = C dv/dt`):

```
trapezoidal:    Geq = 2C/dt           I_hist = -Geq * v_prev - i_prev
backward Euler: Geq = C/dt            I_hist = -Geq * v_prev
```

Series R-C (`EmtCompanionStamp.SeriesRc`; `v = R i + v_C`, `i = C dv_C/dt`). With `z` the capacitor's companion
resistance, `z = dt/(2C)` trapezoidal and `z = dt/C` backward Euler, the capacitor obeys
`v_C(n) = z i(n) + memory`, and `v_C(n-1) = v(n-1) - R i(n-1)` gives

```
trapezoidal:    Geq = 1 / (R + dt/(2C))
                I_hist = -Geq * v_prev + Geq * (R - dt/(2C)) * i_prev
backward Euler: Geq = 1 / (R + dt/C)
                I_hist = -Geq * v_prev + Geq * R * i_prev
```

With `R = 0` the series R-C rule equals the capacitor rule algebraically, and differs in the last bit, so a shunt
capacitor keeps its own factory. A negative `R` (a network-reduction equivalent) is accepted; a resistance that
cancels the companion resistance makes `Geq` infinite and the engine refuses it by name.

Off-nominal tap. A tapped series arm (`Tap != 1`) is an ideal `Tap : 1` transformer on the From side followed by
the arm. With `S = 1/Tap` the arm sees `S * v_K - v_M`, node K carries `S * i`, node M carries `i`:

```
arm voltage     v_arm = S * v_K - v_M
arm current     i = g * v_arm + I_hist                       (g = Geq of the arm's companion)
stamp           (K,K) += g * S^2     (M,M) += g     (K,M) = (M,K) += -g * S
right-hand side node K: -= S * I_hist      node M: += I_hist
Dirichlet M     rhs[K] += g * S * v_dir[M]       Dirichlet K     rhs[M] += g * S * v_dir[K]
```

At `w0` this reproduces the power-flow stamp `Yff = ys/a^2`, `Yft = Ytf = -ys/a`, `Ytt = ys` for a real tap
`a = Tap`. The From-end charging capacitor is `C_end / a^2` so that `Yff = (ys + jb/2)/a^2` holds as well. A branch
current probe on a tapped arm reads the arm current `i` (the To-side current before the To-end charging); the From
bus current is `S * i`. An untapped arm (`Tap == 1`, built by `a == 1.0`) takes the original stamping code path,
which keeps untapped networks bit-identical to their pinned digests.

Sources.

```
ideal   (Rs <= 0):  bus is a Dirichlet node, v = Emf(t), eliminated from the unknowns
Norton  (Rs >  0):  conductance 1/Rs to ground (a resistor branch) and injection Emf(t)/Rs
current source:     injection Inj(t) at the bus
CLI source:         Emf(t) = amp * cos(2*pi*freq*t)  for freq > 0,   Emf(t) = amp  for freq = 0 (DC)
```

Bergeron line (`BergeronLine`; lossless distributed line). `Yc = 1/Zc`. `N = max(1, round(tau/dt))` whole
steps, fixed at construction. The line stamps `Yc` on the diagonal at each free end and nothing off the diagonal:
the ends couple only through the delay buffers `BufK` and `BufM` (length `N`, read and written at slot
`Pos`, `Pos = (Pos + 1) mod N`).

```
right-hand side      rhs[K] -= BufK[Pos]          rhs[M] -= BufM[Pos]
after the solve      i_km = Yc * v_k + BufK[Pos]  i_mk = Yc * v_m + BufM[Pos]
history write        BufK[Pos] = -Yc * v_m - i_mk
                     BufM[Pos] = -Yc * v_k - i_km
                     Pos = (Pos + 1) mod N
probe                i_km (current from K into the line) at the last step
```

A wave that leaves one end at step `n` is read at the other end at step `n + N`, so the modelled travel time is
`N * dt`. A matched line absorbs the wave without reflection; an open end doubles it.

### Building a network from a `GridModel`

`EmtNetwork.FromGridModel(model, EmtBuildOptions)` builds a single-phase network with one node per bus. The
options are `Sources` (at least one, `EmtSourceSpec(BusId, Rs = 0, AmplitudePu = 1, FrequencyHz = 0, PhaseDeg = 0)`),
`IncludeLoads = true`, `UseTravellingWaveLines = false` and `NegativeDemand = ConstantCurrent`. Element ids are
assigned from 0 in build order: branches first (series arm, then its charging shunts), then loads, then bus
shunts, then sources.

Every in-service branch is built so that it reproduces the power flow's branch stamp at `w0` (`EmtTapAndSeriesCapacitorTests` checks it against `AdmittanceStamp.Branch`):

- The tap `a` is `TapRatio`, and `TapRatio <= 0` means 1.
- Travelling-wave mode (`UseTravellingWaveLines`) applies to a branch that is not a transformer, has `B > 0`,
  `X > 0` and `a == 1`. It becomes a `BergeronLine(Zc, tau)`, lossless: the series `R` and the separate charging
  shunts are not built for it. Any other branch is built lumped, so a tap is never lost to the Bergeron model.
- `X < 0`: `SeriesCapacitorBranch(R, C = 1/(|X|*w0), a)`.
- `X >= 0`: `SeriesBranch(R, X/w0)` when `a == 1`, else `SeriesBranch(R, X/w0, a)`. `X = 0` with `R != 0` is a
  pure resistance arm.
- `B > 0`: capacitors `(B/2)/w0` at the To end and `(B/2)/w0/a^2` at the From end. `B < 0`: shunt reactors
  `L = 1/((|B|/2)*w0)` at the To end and `a^2` times that at the From end, so the susceptance at `w0` is exactly
  `-|B|/2` per end and no negative capacitance is ever built.
- Loads (`IncludeLoads`): a bus with `Pd == 0` and `Qd == 0` gets none; otherwise a constant-impedance shunt
  `G = Pd/BaseMva` with an inductor for `Qd > 0` or a capacitor for `Qd < 0`. A bus with `Pd < 0` would make a
  negative conductance, which is not passive. `NegativeDemand = ConstantCurrent` (the default) builds
  instead a `CurrentSource` injecting `|Pd|/BaseMva` pu, shaped like the first source's EMF at unit amplitude
  (a constant for DC, `cos(2*pi*f*t + phase)` for AC), with the reactive demand still a passive L or C.
  `NegativeDemand = Refuse` throws an `ArgumentException` listing every bus with `Pd < 0`.
- Bus shunts: `G = Gs/BaseMva`; `Bs > 0` a capacitor, `Bs < 0` a reactor.
- Sources: `EmtSourceSpec` becomes a `VoltageSource` with the waveform above and phase `PhaseDeg`.
- A model with `BaseMva <= 0` gets no loads and no bus shunts.

The build refuses with an `ArgumentException`. The message names a branch as `branch #index (from->to, 'name')`, the buses for the negative-demand row and the bus for the source-bus row:

| Condition | Message states |
|---|---|
| no source in `Sources` | an EMT build needs at least one source |
| in-service branch whose From or To bus is not in the model | the branch is in service but the named end bus is not in the model; out-of-service branches are skipped without a check |
| `PhaseShiftDeg != 0` | a phase-shifting transformer cannot be represented by a real-valued single-phase network |
| `R = 0` and `X = 0` | a zero-impedance coupler, an infinite conductance |
| `NegativeDemand = Refuse` and a bus with `Pd < 0` | lists each such bus with its `Pd` |
| source bus not in the model | `source bus N is not in the model` |

`EmtNetwork.SeriesBranchIds` lists `(From, To, ElementId)` of every series arm, Bergeron line included, so a
caller can resolve a branch-current probe by its end buses. `EmtNetwork.BusToNode` maps bus id to node index.

## Algorithm

### Construction

`new EmtEngine(network, dt, options)` (`EmtOptions.Rule = Trapezoidal`, `EmtOptions.UseCda = true` by default):

1. Refuse `dt <= 0` or non-finite `dt` (`ArgumentOutOfRangeException`).
2. Walk the elements in order and build the branch table. A `SeriesBranch` is refused (`ArgumentException`) for a
   non-finite `R` or `L`, for `L < 0` (a capacitive arm must be a `SeriesCapacitorBranch`), for `R = L = 0`, and for
   a tap that is not a positive finite ratio. A `SeriesCapacitorBranch` is refused for a non-finite `R` or `C`, for
   `C <= 0`, and for a bad tap. A `ShuntElement` becomes one branch to ground per non-zero part (`G` resistor, `C`
   capacitor, `L` inductor). A `SwitchElement` becomes a resistor branch with `Gfixed = 1/Ron` when
   `InitiallyClosed`, else `1/Roff`, and is registered in the switch map. A Norton source adds a resistor branch
   `1/Rs` and an injector `Emf/Rs`; an ideal source adds a Dirichlet node; a `CurrentSource` adds an injector. A
   `BergeronLine` adds a line record with `N = max(1, round(tau/dt))` and two zeroed delay buffers.
3. Number the free nodes in node order, skipping Dirichlet nodes.
4. Record the as-built switch conductances for `Reset`.
5. `Refactor()` (the first factorisation), then `InitializeRest()`.

A branch carries history when it is a shunt or series inductor (`L > 0`), a capacitor, or a series R-C arm;
resistors, switches and pure-R arms do not.

### Assembly, sparsity pattern, ordering and factorisation

`Refactor()` rebuilds `G` from scratch for the present `dt` and rule:

1. Compute `Companion` for every branch with `CompanionFor` (resistor: `Gfixed`; the others by the factories
   above). A non-finite `Geq` throws `InvalidOperationException` naming the element id, its nodes and `dt`.
2. Stamp into a `CsrMatrix.Builder(n_free)`. An untapped branch adds `g` at `(K,K)` and `(M,M)` and `-g` at
   `(K,M)` and `(M,K)`, each only where the node is free. A tapped branch adds `g*S^2`, `g`, `-g*S`, `-g*S`. A
   Bergeron line adds `Yc` at `(K,K)` and `(M,M)` only.
3. `Builder.Build()` sorts each row by column with `Array.Sort` keyed on the column alone, sums the entries of a
   duplicated cell in insertion order (the row key is column then insertion index, so the order holds at every row length), drops added values equal to zero, and inserts an explicit diagonal entry in every row.
4. The ordering is `SparseLu.Ordering(G)`, reverse Cuthill-McKee on the structurally symmetrised graph: components
   start from a minimum-degree node, each node's unvisited neighbours are queued in ascending degree, and the
   visit order is reversed. It is a pure function of the sparsity pattern. The engine caches the ordering with
   the `RowPtr` and `ColIdx` arrays it was computed for and reuses it whenever a later `Refactor()` produces
   identical arrays (compared by content).
5. `SparseLu.FactorWithOrdering(G, ordering)` converts to column form (exact zeros are dropped) and runs
   Gilbert-Peierls left-looking LU with partial pivoting: the column permutation is the RCM ordering, the row
   pivot of each column is the largest `|x_i|` over the rows that are still unpivoted. A pivot at or below
   `NumericTolerances.RelativePivotFloor * max|G_ij|` (`1e-12` times the largest entry) returns null and the
   engine throws `InvalidOperationException("EMT nodal matrix G is singular ...")`. A non-finite companion conductance is
   refused before the factorisation, with `InvalidOperationException`.
6. Size the per-step solve buffers and increment `Refactorizations`.

The sparsity pattern is the graph of the free nodes: one diagonal entry per free node, and one symmetric pair of
off-diagonal entries for every pair of free nodes joined by a series arm, parallel arms merging into one cell.
Shunts, sources, switches to ground and Bergeron lines add only diagonal entries. An open switch stays in the
pattern as the conductance `1/Roff`, which is never zero, so a switching event changes numbers and not the
pattern.

Refactorisation happens in exactly these places, and nowhere else:

| When | Refactorisations |
|---|---|
| Construction | 1 |
| `Reset()` | 1 (the ordering cache is kept; the pattern is unchanged) |
| A switching event that changed a conductance, CDA on and base rule trapezoidal | 2 (to `dt/2` backward Euler, back to `dt` trapezoidal) |
| A switching event that changed a conductance, any other configuration | 1 (at the present `dt` and rule) |
| An event that changed nothing (closing a closed switch, opening an open one) | 0 |
| Several events due at one step | counted once for the step |

`EmtRunStats.Refactorizations` therefore equals `1 + 2 * (event steps with CDA) + (event steps without CDA)`. The
step `dt` never changes within a run, so a refactorisation is never caused by anything but a switching event.
The conductance matrices of the two CDA refactorisations are bit-identical to the trapezoidal `G`: for every
reactive branch the backward-Euler value at `dt/2` is the same correctly rounded quotient as the trapezoidal value
at `dt` (`l/(dt/2) = (2*l)/dt`, `c/(dt/2) = (2*c)/dt`, `(dt/2)/c = dt/(2*c)`, and halving `dt` is exact), so only
the history coefficients differ between the rules.

### Rest initial condition

`InitializeRest()` runs at construction and in `Reset()`. It seeds every reactive branch history so that a source
already on at `t = 0` gives the correct step response instead of the half-di/dt artefact of an all-zero history.

1. Fixed nodes. Every Dirichlet node is fixed at `Emf(0)`. Every node with a capacitor to ground is fixed at 0 V
   (an uncharged capacitor holds its node at 0 V at rest).
2. Floating nodes. Union-find over the branches that conduct at rest, restricted to free-to-free pairs: a
   resistor or switch, a series arm with `L <= 0` (a pure-R arm), and a series R-C arm with `R != 0`. A component
   is anchored when one of its nodes has a conducting branch to ground or to a fixed node. A switch that is open
   at construction (or after `Reset()`) neither joins nor anchors: its open conductance `1/Roff` (1e-9 pu by
   default) stands in for "not connected". Were it counted, an island tied to ground only by an open fault switch would
   enter `G_IC` held by 1e-9 S while the same matrix carries 1e5 S elsewhere (pglib case240, fault on bus 8034: bus 8034
   joins the network only through an inductive arm, and series capacitors with `R = 1e-5` conduct as `1/R`), past
   the sparse LU's pivot floor, which is relative to the largest entry. Such an island is pinned like any other floating
   part. An island anchored another way keeps the open switch's stamp in `G_IC`. Every node of an
   unanchored component is pinned at 0 V, and the number pinned is
   `EmtEngine.InitialConditionFloatingNodes`. Inductive arms are open at rest, so a bus joined to the network only
   through inductive arms, a transformer-only bus, is floating. Bergeron lines are not branches, so they do not
   anchor a node.
3. Solve. Free nodes are numbered, `G_IC` is stamped from the branches that conduct at rest (tap-aware: `g*S^2`,
   `g`, `-g*S`) with `g` the companion conductance (for a series R-C arm, `1/R`), the right-hand side collects the
   injectors evaluated at `t = 0` and the coupling to fixed nodes, and `SparseLu.Factor` solves it. A singular
   `G_IC` throws `InvalidOperationException("EMT initial-condition matrix is singular over n node(s) even after
   pinning m floating node(s) ...")`.
4. Seed. For every branch with history, `prevV` is the branch voltage from the rest solution (`S*v_K - v_M` for a
   tapped arm) and `prevI = 0`. A series R-C arm at rest is an uncharged capacitor, a short, in series with `R`:
   with `R != 0` it conducts as `1/R`, is seeded with `prevI = (1/R) * v_arm` and `prevV = R * prevI` (so
   `v_C = 0` exactly); with `R = 0` it is open in the rest solve and seeded with `prevI = 0`, `prevV = 0`.
5. Capacitor currents at `t = 0+`. A node held at 0 V by shunt capacitors still obeys Kirchhoff's current
   law: the injectors at that node (current sources, including the negative-demand current sources of
   `FromGridModel`, and Thevenin sources' Norton currents) evaluated at `t = 0`, plus the current of every branch
   that conducts at rest from a node at a non-zero rest voltage, can only flow into its capacitors. Each such
   capacitor is seeded with `prevI` = that inflow times `C_k / sum C` (parallel capacitors share by capacitance;
   one capacitor takes it all, exactly). A zero seed would make the trapezoidal rule take the injection as ramping up
   over the first step and integrate half a step of charge too little: a lasting offset of `-I*dt/(2C)` on the node.
   A node with zero inflow keeps the `+0.0` seed, so a network with no injection or resistive current into a
   capacitor-held node is unaffected by this step. Backward Euler's capacitor history does
   not read `prevI`, so it is unaffected. Not covered: an injection at a pinned floating node (no conducting path at
   rest, so no defined destination) and an ideal series capacitor (open in the rest solve).

The rest solve does not write the node-voltage array that probes read. The `t = 0` sample is the de-energised
state just before the sources switch on (`t = 0-`): a probe reads `0` for every node voltage there, including an
ideal source's own node, and `0` for every branch current except a conducting series R-C arm, which shows its
seeded rest current. Sources and injections are on from `t = 0+`; their effect enters the trajectory through the
seeded histories above and appears from the first stepped sample, which is the first solved voltage.


### The time loop

`EmtEngine.Simulate(EmtCase)` returns the whole trajectory. `EmtCase(Network, DtSeconds, DurationSeconds, Probes,
Events)` repeats the network and `dt` the engine was built with, and both are checked (next section).

Setup: `steps = round(DurationSeconds / dt)`; `eps = dt * EventTimeToleranceFraction`; the events are sorted by
time with a stable sort; every event is validated before any step; `_advanced = true`. The `t = 0` point is
recorded. Then for `step = 1 .. steps`, with `t = step * dt` (a product, never an accumulating sum):

1. Apply every event whose `AtSeconds <= t + eps`, in sorted order. `fired` is true when at least one event
   changed a switch conductance.
2. If `fired` and CDA is on and the base rule is trapezoidal: `SetIntegration(dt/2, BackwardEuler)`;
   `Step(t - dt/2, advanceLines: false)`; `Step(t, advanceLines: true)`; `SetIntegration(dt, base rule)`.
   Otherwise: if `fired`, `Refactor()`; then `Step(t)`.
3. Read every probe, update the running `max |probe|`, append the point.

`Step(t, advanceLines)`:

1. `v_dir[node] = Emf(t)` for every Dirichlet node.
2. `rhs = 0`; add `Inj(t)` of every injector at a free node (a Norton source adds `Emf(t)/Rs`).
3. For every Bergeron line subtract `BufK[Pos]` and `BufM[Pos]` from the end nodes' right-hand sides.
4. For every branch: `ih = History ? comp.History(prevV, prevI) : 0`; store `ih`; stamp the right-hand side
   (untapped: `rhs[K] -= ih`, `rhs[M] += ih`, plus `g * v_dir` of a Dirichlet end; tapped: `rhs[K] -= S*ih`,
   `rhs[M] += ih`, plus `g*S*v_dir`).
5. `SolveInto(rhs, vFree, work)`: forward and back substitution on the stored `L`, `U` and permutations.
   Allocation-free, scalar IEEE-754 double arithmetic with no explicit fused multiply-add.
6. Assemble the node voltages: free nodes from the solution, Dirichlet nodes from `v_dir`.
7. For every branch: `v_B = v_K - v_M` (`S*v_K - v_M` tapped); `prevV = v_B`; `prevI = Geq * v_B + ih`.
8. For every line: compute `i_km`, `i_mk`; record `i_km` as the line's probe current; when `advanceLines` is true,
   write the two history slots and advance `Pos`.

The loop runs under `GcScope.LowLatency()`. Probes: `EmtProbeKind.NodeVoltage` reads the node voltage;
`EmtProbeKind.BranchCurrent` reads `prevI` of the branch whose `ElementId` equals the probe target (a shunt
element with several parts answers with its first part: `G`, then `C`, then `L`), or the line's `i_km`, and
`NaN` when the id names neither. `EmtResult` carries the probe names, the points (`steps + 1`, instants
`step * dt`) and `EmtRunStats(Steps, Refactorizations, MaxProbeAbs, WallMs)`. `MaxProbeAbs` covers the stepped
samples and not the `t = 0` sample. `WallMs` is wall-clock time of the loop and differs from run to run; it is
not part of the numerical result. A NaN or infinite probe value, at `t = 0` or at any step, throws
`InvalidOperationException` ("non-finite value in trajectory (numerical failure)", naming the probe and the instant)
at the first step it appears; no trajectory with a non-finite value is returned (the CLI maps it to exit 3).

Manual stepping (`EmtEngine.Step(t)`) advances the state and marks the engine advanced.

### Switching events and CDA

`SwitchEvent(AtSeconds, ElementId, Close)` (`src/GridSim.Core/Dynamics/Emt/EmtEvent.cs`) closes a switch
(`Gfixed = 1/Ron`) or opens it (`1/Roff`). `EmtEvent` is the abstract base; the engine applies only
`SwitchEvent`.

Timing. An event is applied at the first step whose time satisfies `t >= AtSeconds - eps`, with
`eps = 1e-6 * dt` (`EmtConstants.EventTimeToleranceFraction`). The tolerance absorbs the rounding of
`t = step * dt`, so an event authored exactly on a step boundary is not deferred a step. An event between steps
takes effect at the next step boundary with no interpolation: the new conductance acts over the whole interval
`(t - dt, t]`. An event at or before `t = 0` is applied at step 1. An event after the end of the run is never
applied. Events at the same instant keep the order given.

Validation before the first step. Every event must be a `SwitchEvent` whose `ElementId` names a switch
(`ValidateEvents`). Otherwise the engine throws `ArgumentException` listing each offending event and the valid
switch ids, and the engine is left unadvanced. A series branch id given to `switch:<id>@t:open` is the standard
mistake this catches.

No-op detection. `ApplyEvent` returns false when the requested conductance equals the present one
(`Gfixed == g`), so re-closing a closed switch triggers neither a refactorisation nor a CDA burst, and the
trajectory is bit-identical to the run without that event.

Critical Damping Adjustment. The trapezoidal rule is marginally stable: a discontinuity in a branch voltage or
current excites a numerical oscillation at the Nyquist frequency (`1/(2 dt)`) that the rule does not damp, because
its amplification factor on the stiff mode is `-1`. Backward Euler is L-stable, amplification factor `0`, so it
annihilates that mode. CDA runs the step that contains the discontinuity as two backward-Euler half-steps:

```
trigger   an event step with fired = true, UseCda = true and base rule = Trapezoidal
burst     SetIntegration(dt/2, BackwardEuler)     refactor (1)
          Step(t - dt/2, advanceLines: false)     sources at t - dt/2; line buffers read, not written
          Step(t,        advanceLines: true)      sources at t;        line buffers written, Pos advanced
          SetIntegration(dt, base rule)           refactor (2)
```

The burst covers exactly one `dt`, so the time axis is unchanged and every later step is a normal trapezoidal
step from the state the burst left. The line history is frozen across the first half-step so a Bergeron line
advances one buffer slot per full step regardless of the burst. With the base rule backward Euler, or with
`UseCda = false` (`--no-cda`), the event step is a single ordinary step after one refactorisation. The worked
example below measures the effect.

### Case consistency, reset and refusals

The companions and the Bergeron delay counts are fixed at construction, so a case must describe the engine it is
run on. `Simulate` checks, in this order, before any state moves:

1. `c.DtSeconds` must equal the construction `dt` exactly: `Double.Equals`, so one ulp is a different step and NaN
   is refused. `ArgumentException` naming `DtSeconds`.
2. `c.Network` must not be null (`ArgumentNullException`), must be the engine's own object, or must match it by
   value: the same node count, the same number of elements, and element by element `Equals`. A `VoltageSource` or
   `CurrentSource` contains a function, which cannot be compared, so for those the `Id`, `Bus` and (for a
   `VoltageSource`) `Rs` are compared and the engine's own function is the one that runs.
   `ArgumentException` naming the first difference.
3. The engine must not have advanced: a second `Simulate`, or a `Simulate` after a manual `Step`, throws
   `InvalidOperationException` telling the caller to `Reset()` or build a new engine.
4. The events are validated (previous section), and every event time must be finite, at or after 0 and at or before
   the last step's time (the loop's own tolerance); an event in `[0, dt)` applies at the first step
   (`ValidateEventTimes`).
5. The duration must give at least one step: `round(duration / dt)` from 1 to `int.MaxValue - 1` (`StepCount`); a
   duration of 0, a negative or non-finite one, or one below half a step is refused (`ArgumentException` naming
   `DurationSeconds`).
6. Every probe must name something it can record (`ValidateProbes`): a node probe a node `0..NodeCount-1`; a
   branch-current probe a series R-L or R-C arm, a switch, a Bergeron line or a shunt with exactly one part. A
   multi-part shunt (each part is its own stamped branch) and a source (its stamped branch is not the source current)
   are refused.

A refused case leaves the engine unadvanced, so the correct case still runs. `Reset()` restores every switch to
its construction state, empties the Bergeron buffers, restores the construction `dt` and rule, clears every
history and the right-hand-side and node-voltage buffers, zeroes `Refactorizations`, refactors and re-runs the
rest solve; a run after `Reset()` is bit-identical to a fresh engine's. `EmtEngine.Network` and
`EmtEngine.DtSeconds` expose what the engine was built with.

The refusals of the single-phase path:

| Where | Condition | Exception |
|---|---|---|
| `EmtNetwork` constructor | `nodeCount <= 0` | `ArgumentOutOfRangeException` |
| `FromGridModel`, `EmtEngine` constructor, `Simulate` | null model, options, network or case | `ArgumentNullException` |
| `FromGridModel` | no source, dangling in-service branch, phase shifter, `R = X = 0`, refused negative demand, unknown source bus | `ArgumentException` naming the branch or bus |
| `EmtUnits.SeriesC` | reactance not negative | `ArgumentOutOfRangeException` |
| `EmtEngine` constructor | `dt <= 0` or non-finite | `ArgumentOutOfRangeException` |
| `EmtEngine` constructor (`ValidateNetwork`, before any stamping; every offender of a kind listed in one message) | duplicate element ids; a node out of range (a one-terminal element on ground, or past the last node); two ideal voltage sources on one bus; a source `Rs` negative or not finite; a switch `Ron` or `Roff` not positive finite; a shunt `C` or `L` negative or non-finite, or a non-finite `G`; a Bergeron surge impedance or travel time not positive finite | `ArgumentException` |
| `EmtEngine` constructor with `EmtOptions.RefuseNonPassive` | any non-passive element: a series arm with negative `R`, a shunt with negative `G` (listed by default in `EmtEngine.NonPassiveElements`) | `EmtNonPassiveNetworkException` (an `ArgumentException`) |
| `EmtEngine` constructor | `SeriesBranch`: non-finite `R` or `L`; `L < 0`; `R = L = 0`; tap not positive finite | `ArgumentException` naming the element |
| `EmtEngine` constructor | `SeriesCapacitorBranch`: non-finite `R` or `C`; `C <= 0`; tap not positive finite | `ArgumentException` naming the element |
| `Refactor` (construction, event, burst) | a branch whose `Geq` is not finite (zero resistance, `Ron = 0`, `Roff = 0`, a resistance cancelling the companion resistance) | `InvalidOperationException` naming element, nodes and `dt` |
| `Refactor` | `G` singular (pivot at or below the relative floor) | `InvalidOperationException` |
| `Refactor` | a non-finite companion conductance in `G` | `InvalidOperationException` |
| `InitializeRest` | `G_IC` singular after pinning | `InvalidOperationException` |
| `Simulate` | `DtSeconds` differs from construction | `ArgumentException` |
| `Simulate` | network differs | `ArgumentException` |
| `Simulate` | engine already advanced | `InvalidOperationException` |
| `Simulate` | event not a `SwitchEvent`, or id not a switch | `ArgumentException` listing valid switch ids |
| `Simulate` | an event time outside the run; a duration with no step; a probe that names nothing | `ArgumentException` |

Three refusals as the verb prints them (captures `refuse-non-switch-event.txt`, `refuse-phase-shifter.txt` and
`refuse-bad-rule.txt` in `docs/_captures/emt/`):

```
$ gridsim emt case14 --duration 0.01 --event switch:3@0.005:open
emt: EMT case has events that cannot act: switch event at 0.005 s names element 3, which is not a switch. Valid switch ids: none.
[exit 2]
```

```
$ gridsim emt cigre --duration 0.01
emt: EMT build: branch #12 (0->1) is a phase-shifting transformer (shift 30 deg). A phase shift multiplies the voltage phasor by a complex number, which a real-valued single-phase time-domain network cannot represent; it would be silently dropped. Remove the shift or use a phasor study. (Parameter 'model')
[exit 2]
```

```
$ gridsim emt case14 --rule foo
error: --rule expected trap or be but got 'foo'
[exit 2]
```

### Passivity and singular systems

The engine has no modal or eigenvalue analysis. Stability is that of the discretisation: trapezoidal integration
is A-stable and backward Euler is L-stable, so a passive network (non-negative `G`, `C`, `L`, `R`) gives a bounded
trajectory. A network that contains a negative resistive or reactive element feeds energy into its natural modes
and grows without bound; the builder therefore never creates one from negative charging or negative demand. Negative
demand becomes a bounded current source (or is refused), and negative charging becomes a shunt reactor. A negative
series resistance or shunt conductance that the case data itself carries (a network-reduction equivalent: IEEE 145
has 224 negative-resistance branches and 8 negative-conductance shunts, pglib `case588` 5 negative-resistance
branches) is built as given and listed in `EmtEngine.NonPassiveElements`; `EmtOptions.RefuseNonPassive` refuses it by
name, and the `emt` verb sets that option unless `--allow-non-passive` is given. The conductance matrix is real and
symmetric in value, and a tapped arm stamps `-g*S` on both off-diagonals; the LU does not assume
positive-definiteness, because partial pivoting handles the indefinite matrices that negative-`R` equivalents
produce.

A defective or singular system fails loud rather than producing a result. A nodal matrix without a pivot above
`1e-12` of its largest entry is singular and `Refactor` throws; every inductor and capacitor contributes a positive
`Geq`, so a free node that is joined by any branch to ground or to a Dirichlet node does not make the matrix
singular. The rest-solve matrix is guarded separately: nodes that have no
conducting path at rest are pinned (counted, reported), and a matrix that is still singular throws.

### Three-phase engine (programmatic API only)

`ThreePhaseEmtEngine(ThreePhaseNetwork net, double dt, double frequencyHz = 50)` is the abc generalisation of
the scalar engine. It has no CLI verb and no builder from a `GridModel`: it is reached from code, and the tests
listed under Invariants are its callers. It runs once (`Simulate` a second time throws `InvalidOperationException`).

```
ThreePhaseNetwork(BusCount, Sources, Branches, Shunts, Faults, Converters = null)
  ThreePhaseSource(Bus, AmplitudePu = 1, FreqHz = null)        phase p: Amplitude*cos(2*pi*f*t - p*120 deg); null f = the engine's
  ThreePhaseBranch(FromBus, ToBus, Rs, Ls, Rm = 0, Lm = 0)     symmetric self (Rs,Ls) and mutual (Rm,Lm)
  ThreePhaseShunt(Bus, G = 0, C = 0, L = 0)                    per phase, uncoupled
  ThreePhaseFault(Bus, Phases, Rf, AtSeconds, ClearSeconds)    shunt fault to ground on the listed phases
  ThreePhaseConverter(Bus, Unit, PllBandwidthHz = 20, FilterSusceptancePu = 0.05, StartupRampSeconds = 0.02)
engine.Simulate(duration, probeBus) -> (double[] T, double[][] Vabc)       one row per step, phases a, b, c
engine.ConverterTraces              -> per-converter EmtConverterTrace
engine.Refactorizations
```

Each bus is three nodes, `3*bus + p`. A coupled branch stamps the 3x3 companion, so inter-phase mutual coupling is
exact:

```
A = R + (2/dt) L            3x3, self on the diagonal, mutual off the diagonal
Y = inverse(A)              closed-form 3x3 inverse; |det| < 1e-300 throws InvalidOperationException
Alpha = Y * ((2/dt) L - R)
I_hist = Y * v_prev + Alpha * i_prev          (3-vectors)
i = I_hist + Y * v_branch                     stamped as Y, Y, -Y, -Y between the K and M phase nodes
```

Shunts use the same scalar companions as the single-phase engine, trapezoidal only. A fault is a conductance to
ground, `1/Rf` when `AtSeconds - tol <= t < ClearSeconds - tol` (`tol = dt * EventTimeToleranceFraction`) and
`EmtConstants.OpenFaultConductance` (`1e-9` pu) otherwise; it sits in the pattern throughout. Any change of any
fault conductance triggers one `Refactor()`. The three-phase engine has no critical damping adjustment and no
rest solve: every history starts at zero, and the ideal sources are Dirichlet nodes. The ordering cache and the
solve buffers work as in the scalar engine.

Converters (`ThreePhaseConverter`) are average-value models. A grid-following converter is a balanced
three-phase current source whose phasor comes from the same control law as the RMS engine
(`Models.RmsConverterState`: K-factor reactive boost, current limit with reactive priority, LVACM, active-current
freeze with ramp-limited recovery, blocking for a line-commutated terminal), so the two engines agree by
construction ([ch. 24](<0024 Standard dynamic-model library.md>)). Per step:

1. Injection: `i_p = ramp * (Re(I) cos(a_p) - Im(I) sin(a_p))`, `a_p = w0 t - p * 120 deg`,
   `ramp = min(1, t / StartupRampSeconds)`, added to the right-hand side at the phase node (one-step explicit).
2. After the solve, an SRF-PLL transforms the terminal voltage to the dq frame on its own angle (amplitude-invariant
   `2/3` Park transform) and advances `PllInt += Ki*vq*dt`, `Theta += (w0 + Kp*vq + PllInt)*dt` with
   `Kp = 2*zeta*wn/V0`, `Ki = wn^2/V0`, `wn = 2*pi*PllBandwidthHz`, `zeta = 0.707`
   (`ConverterDefaults.PllDampingRatio`), `V0 = max(|V0|, 1e-3)`.
3. The fundamental magnitude is the Park magnitude low-pass filtered with
   `VFilt += (|v_dq| - VFilt) * (1 - exp(-dt / 5 ms))` (`ThreePhaseEmtEngine.MeasurementFilterSeconds`). Dip logic
   and LVACM act on `VFilt`, never on the raw magnitude.
4. The law is stepped and the current phasor is phased on the PLL angle, never on the instantaneous measured angle.

Each converter also carries a per-phase AC filter capacitor `FilterSusceptancePu * RatingPu / w0` and the 20 ms
soft start. A grid-forming unit (`Unit.Parameters.GridForming`) is an EMF behind its virtual reactance: a per-phase
shunt inductor `Lv = Xv / Rating / w0` plus the Norton current `E * y`; its power is measured from the actual
terminal current (injection minus the inductor current) in the PLL frame and filtered at 5 ms; its current limit is
armed at `t >= 2 * StartupRampSeconds + 0.05 s` on the filtered current and released when the unlimited current
falls below `0.95 * Imax`; while limited it injects the limited current plus the inductor's own last current.

## The `emt` verb

`gridsim emt` drives the single-phase engine; the flags are in
[the CLI reference](<../manual/0019 CLI reference.md#emt>). The driver does the following, in order, and exits with
the code shown on the first failure.

1. Load the case. A load error exits 1. The default case is `uk`.
2. Read `--dt-us` (default 20; must be positive, `dt = dtUs * 1e-6` in binary floating point, which makes 20 us
   `1.9999999999999998e-05` s), `--duration` (0.1 s; must be above 0 and give at least one step), `--amp` (1),
   `--freq` (0, DC; below 0 is refused), `--rule` (`trap` or `be`; anything else is refused), `--source` (default:
   the first generator's bus, else the first bus). A bad numeric or enum flag exits 2 with
   `error: --flag expected ... but got '...'`.
3. Build the network with an ideal source (`Rs = 0`) at the source bus, loads on unless `--no-loads`, Bergeron
   lines when `--lines`. A build refusal exits 2 with the message.
4. Parse every `--event`. `fault:<bus>@<t>:<Rpu>` (`Rpu` a positive finite number; a bolted fault is a small one,
   for example `1e-4`) adds one `SwitchElement(id, node, Ground, Ron = Rpu, Roff = 1e9, InitiallyClosed = false)` per
   distinct (bus, resistance), with ids from `CliDefaults.EmtFaultSwitchIdBase = 1000000` in token order of first
   appearance, and a `SwitchEvent(t, id, Close: true)`. A `fault:` at a bus opens that bus's other fault switches at
   the same instant, so a second `fault:` changes the fault's resistance; two faults at one bus at the same instant
   exit 2. `clear:<bus>@<t>` opens every fault switch of the bus and needs a `fault:` for that bus anywhere on the
   line. `switch:<id>@<t>:close|open` addresses an element id directly; `FromGridModel` builds no switch, so the ids
   that exist are the fault switches. The action word must be `close` or `open` (any letter case). Every event time
   must lie in the run. Every problem on the line is listed, then the run exits 2; a refusal starts `error: emt:`.
5. Resolve the probes. `v:<bus>` and `i:<from>-<to>` (or `i:<from>-><to>`), comma separated; the default is the
   source-bus voltage and the first series branch current. A token that names no bus of the case, no in-service series
   branch in that direction, or is malformed exits 2 naming the token.
6. Build the engine with `EmtOptions { Rule, UseCda = !--no-cda, RefuseNonPassive = !--allow-non-passive }` and
   `Simulate`. A non-passive network exits 2 naming its first 10 non-passive elements and suggesting
   `--allow-non-passive`; with the flag it runs and a warning gives the count. `ArgumentException` (an event naming a
   non-switch, a refused element) exits 2; `InvalidOperationException` (singular matrix, non-finite conductance,
   refused IC) exits 3.
7. Report `engine.InitialConditionFloatingNodes` on stderr when it is above zero; the run continues.
8. A non-finite probe value anywhere in the trajectory exits 3.
9. Write `<out>/timeseries.csv` (provenance header lines starting `# `, then `t_seconds,<probe names>` and one row
   per point, values with 9 significant digits), `<out>/summary.md` and `<out>/run-metadata.json`, and print the
   one-line summary `emt <case>: <steps> steps @ <dt>us, <n> factorization(s), peak |probe| <x>, <ms> ms -> <out>`.
   `<out>` defaults to `results/emt-out`. `summary.md` reports the rule and `CDA: on`, `CDA: off (--no-cda)` or
   `CDA: off (not used under backward Euler)`. `run-metadata.json` carries, after the shared sidecar, a `parameters`
   object: `verb`, `case`, `modelName`, `dtMicroseconds`, `durationSeconds`, `steps`, `sourceBus`, `amplitudePu`,
   `frequencyHz`, `rule`, `criticalDampingAdjustment` (what ran), `noCdaFlag`, `travellingWaveLines`, `loads`,
   `allowNonPassive`, `nonPassiveElements` (the count), `probes` (`name`, `kind`, `target`) and `events` (`token`,
   `kind`, bus or switch id, `atSeconds`, `resistancePu`, the switch ids, `action`).

Exit codes: 0 success, 1 case load failure, 2 usage or build error, 3 numerical failure.

## Parameters and defaults

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Time step | 20 | us | Fixed step; set once per engine | `--dt-us` in `Program.cs` (`FlagDouble(a, "--dt-us", 20.0)`) |
| Duration | 0.1 | s | Simulated time | `--duration` in `Program.cs` |
| Source amplitude | 1.0 | pu | EMF amplitude | `--amp`; `EmtSourceSpec.AmplitudePu` |
| Source frequency | 0 | Hz | 0 is DC | `--freq`; `EmtSourceSpec.FrequencyHz` |
| Source resistance | 0 | pu | 0 makes an ideal (Dirichlet) source | `EmtSourceSpec.Rs` |
| Integration rule | trapezoidal | - | `trap` or `be` | `EmtOptions.Rule`, `IntegrationRule` |
| CDA | on | - | Backward-Euler half-steps at a changing event | `EmtOptions.UseCda` |
| Loads | on | - | Constant-impedance load shunts | `EmtBuildOptions.IncludeLoads` |
| Travelling-wave lines | off | - | Bergeron lines for charged non-transformer branches | `EmtBuildOptions.UseTravellingWaveLines` |
| Negative demand | `ConstantCurrent` | - | Bounded current source instead of a negative conductance | `EmtBuildOptions.NegativeDemand` |
| Output directory | `results/emt-out` | - | Result files | `--out` |
| System frequency | 50 | Hz | `w0 = 2*pi*FrequencyHz` for `L`, `C` | `GridModel.FrequencyHz` |
| Event time tolerance | 1e-6 | fraction of `dt` | Event applied when `t >= At - 1e-6*dt` | `EmtConstants.EventTimeToleranceFraction` |
| Switch on resistance | 1e-6 | pu | Closed switch | `EmtConstants.DefaultSwitchOnResistancePu`, `SwitchElement.Ron` |
| Switch off resistance | 1e9 | pu | Open switch (conductance 1e-9 pu) | `EmtConstants.DefaultSwitchOffResistancePu`, `SwitchElement.Roff` |
| CLI fault switch off resistance | 1e9 | pu | Open state of an `--event fault` switch | `CliDefaults.EmtSwitchOffResistance` |
| CLI fault switch id base | 1000000 | - | First element id of `--event fault` switches | `CliDefaults.EmtFaultSwitchIdBase` |
| Open three-phase fault conductance | 1e-9 | pu | `ThreePhaseFault` before and after | `EmtConstants.OpenFaultConductance` |
| Pivot floor | 1e-12 | x max abs G | Pivot at or below this is singular | `NumericTolerances.RelativePivotFloor` |
| 3x3 determinant floor | 1e-300 | - | Singular three-phase branch matrix | `ThreePhaseEmtEngine.Inv3` |
| Bergeron delay | `max(1, round(tau/dt))` | steps | Whole-step travel time | `EmtEngine` constructor |
| PLL bandwidth | 20 | Hz | EMT converter PLL natural frequency | `ConverterDefaults.EmtPllBandwidthHz` |
| PLL damping | 0.707 | - | PLL damping ratio | `ConverterDefaults.PllDampingRatio` |
| Converter measurement filter | 5 | ms | Fundamental-magnitude filter | `ThreePhaseEmtEngine.MeasurementFilterSeconds` |
| Converter filter susceptance | 0.05 | pu of rating | Per-phase AC filter capacitor | `ThreePhaseConverter.FilterSusceptancePu` |
| Converter soft start | 0.02 | s | Injection ramp from zero | `ThreePhaseConverter.StartupRampSeconds` |
| Voltage floor for control | 1e-3 | pu | Divisor floor in the PLL gain and grid-forming current readout | `EmtConstants.MinVoltageForControlPu` |
| Limit arming | `2 * ramp + 0.05` | s | Grid-forming limiter armed after this | `EmtConstants.LimitArmingRampMultiple`, `LimitArmingMarginSeconds` |
| Limit release | 0.95 | x Imax | Grid-forming limiter hysteresis | `EmtConstants.GfmLimitReleaseFraction` |
| Zero-current threshold | 1e-9 | pu | Unlimited current below this limits to zero | `EmtConstants.ZeroCurrentMagnitudePu` |
| Three-phase source frequency | the engine's | Hz | Default `FreqHz` is null, which means the engine's own frequency | `ThreePhaseSource.FreqHz` |

`Ws13DynamicsConstantsAntiDriftTests.Emt_constants_equal_the_literals_they_replaced` pins every `EmtConstants`
value.

## Invariants

Each invariant is pinned by a test in `tests/GridSim.Tests/`.

| Invariant | Test |
|---|---|
| The engine solves the electromagnetic equations: series RLC step response matches the closed form in all three damping regimes | `EmtRlcAnalyticTests.Series_rlc_step_response_matches_closed_form` |
| Trapezoidal error is second order in `dt` | `EmtRlcAnalyticTests.Trapezoidal_error_is_second_order_in_dt` |
| Two runs of the same network agree to 12 decimals | `EmtRlcAnalyticTests.Run_is_deterministic`, `EmtOutOfPhaseSyncTests.Run_is_deterministic` |
| A DC-driven network settles to the resistive solution | `EmtSteadyStateTests.Rl_load_energised_dc_settles_to_the_resistive_solution` |
| Closing onto an LC branch gives a recovery-voltage peak near twice the source; the matrix is refactored at the event | `EmtSwitchingTests.Closing_onto_an_lc_branch_gives_a_trv_peak_near_twice_the_source` |
| CDA suppresses the Nyquist oscillation | `EmtSwitchingTests.Cda_suppresses_the_numerical_nyquist_oscillation` |
| A matched Bergeron line absorbs the wave; an open end doubles it at `tau`; the sending end of an open-ended line reaches 1.0 by 2.5 ms with `tau` = 1 ms | `EmtLineTests.Matched_line_absorbs_the_wave_with_no_reflection`, `EmtLineTests.Open_ended_line_doubles_the_voltage_at_the_far_end` |
| The series R-C companion reproduces the series RLC response (second order trapezoidal, first order backward Euler); with `R = 0` it equals the capacitor rule | `EmtTapAndSeriesCapacitorTests.Series_rc_branch_reproduces_the_closed_form_series_rlc_step_response`, `Series_rc_trapezoidal_error_is_second_order_in_dt`, `Series_rc_backward_euler_converges_at_first_order`, `Series_rc_with_zero_resistance_is_algebraically_the_capacitor_rule` |
| A tapped arm's DC steady state is the ideal-transformer solution | `EmtTapAndSeriesCapacitorTests.Tapped_branch_dc_steady_state_is_the_ideal_transformer_solution` |
| The build maps tap, From-end charging and negative reactance exactly as the power-flow stamp; an untapped branch builds the original element; a tap is never lost to a Bergeron line | `Build_maps_tap_and_from_end_charging_exactly_as_the_power_flow_stamp`, `Build_maps_a_negative_reactance_to_a_series_capacitor_with_the_same_reactance`, `Untapped_branch_builds_exactly_the_original_element`, `Travelling_wave_mode_never_drops_a_tap` (all `EmtTapAndSeriesCapacitorTests`) |
| Untapped cases give bit-identical trajectories: 13 pinned SHA-256 digests, each over every recorded instant of a 40 ms DC run at 20 us and the probes sampled for it (at most 150 evenly strided node voltages and the first 20 branch currents), as raw IEEE-754 bytes (gb-full, uk, case9 and case30, each lumped, with lines and with a fault; case33bw lumped) | `EmtUntappedBitIdentityTests.Untapped_case_emt_trajectory_is_bit_identical_to_the_pre_R078_engine` |
| A case with a different `dt` (one ulp, NaN, double) or a different network is refused, and the refusal leaves the engine unadvanced | `EmtCaseConsistencyTests.A_case_with_a_different_dt_is_refused`, `A_case_with_a_different_network_is_refused`, `Refused_cases_leave_the_engine_unadvanced` |
| An equivalent rebuilt network is accepted and gives bit-identical samples, statistics and `max|probe|` | `EmtCaseConsistencyTests.An_equivalent_rebuilt_network_is_accepted_and_gives_the_same_trajectory` |
| A second `Simulate`, or one after a manual `Step`, is refused; `Reset()` then `Simulate` is bit-identical to a fresh engine (including an initially open switch) | `EmtCaseConsistencyTests.A_second_simulate_without_reset_is_refused`, `Simulate_after_a_manual_step_is_refused`, `Reset_then_simulate_is_bit_identical_to_a_fresh_engine`, `Reset_restores_an_initially_open_switch` |
| The three-phase engine refuses a second `Simulate` | `EmtCaseConsistencyTests.Three_phase_engine_refuses_a_second_simulate` |
| An event naming a non-switch is refused with the valid ids; a no-op event leaves the run bit-identical with no extra refactorisation; a real event refactors | `DistributionAndEmtInputValidationTests.A_switch_event_on_a_non_switch_element_fails_loud`, `A_no_op_switch_event_leaves_the_run_bit_identical_to_no_event`, `A_real_switch_event_still_fires_the_cda_and_refactors` |
| The engine refuses duplicate ids, nodes off the network, two ideal sources on a bus, bad `Rs`, `Ron`, `Roff`, shunt and Bergeron parameters, listing every problem; it lists the non-passive elements (exactly those of IEEE 145 and pglib `case588`, none on the passive bundled cases) and refuses them on request; it refuses event times outside the run, a duration with no step and probes that name nothing | `Doc6EmtEngineTests` (`D024_*`, `D022_*`, `D019_*`, `D025_*`) |
| The verb refuses bad event times, probes, durations, switch words and fault resistances (exit 2), refuses a non-passive case unless allowed, gives a second fault its own resistance, writes the `parameters` metadata and reports whether CDA ran | `Doc6EmtCliProcessTests` (`D019_*` to `D024_*`, `D121_*`, `D122_*`, `D123_*`) |
| A pure-R arm conducts in the rest solve; a floating node is pinned without losing the rest of the IC; a fully anchored network reports 0; a singular IC matrix fails loud | `EmtInitialConditionTests.A_pure_resistance_series_arm_conducts_in_the_initial_condition`, `A_floating_node_is_pinned_and_the_rest_of_the_initial_condition_survives`, `A_network_with_every_node_anchored_reports_no_floating_nodes`, `A_singular_initial_condition_matrix_fails_loud_instead_of_being_skipped` |
| The engine refuses what it cannot carry, naming it: `L < 0`, `R = L = 0`, bad tap, bad `C`, non-finite parameters, a companion that cancels | `EmtTapAndSeriesCapacitorTests` (`Engine_refuses_*`, `Engine_names_a_series_capacitor_whose_negative_resistance_cancels_its_companion`) |
| The build refuses a phase shifter, a zero-impedance branch, a dangling in-service branch; an out-of-service dangling branch is skipped | `EmtTapAndSeriesCapacitorTests.Build_refuses_a_phase_shifter_naming_the_branch`, `Build_refuses_a_zero_impedance_branch_naming_it`; `EmtPassivityAndDanglingBranchTests.In_service_branch_to_a_missing_bus_is_refused_naming_the_branch`, `In_service_branch_from_a_missing_bus_is_refused_naming_the_from_bus`, `Out_of_service_branch_to_a_missing_bus_is_still_skipped` |
| No build creates a negative `G`, `C` or `L` from negative charging or negative demand; `Refuse` lists every offending bus; IEEE 300 builds passive | `EmtPassivityAndDanglingBranchTests.Negative_line_charging_becomes_exact_shunt_reactors_never_a_negative_capacitor`, `Negative_demand_never_builds_a_negative_conductance`, `Refuse_mode_lists_every_negative_demand_bus_and_says_why`, `Bundled_ieee300_builds_a_passive_network_by_default` |
| The `EmtConstants` values | `Ws13DynamicsConstantsAntiDriftTests.Emt_constants_equal_the_literals_they_replaced` |

### Determinism and bit-identity

The single-phase engine is a deterministic sequential program. The element tables are built in element order and
iterated in that order every step; the right-hand side is accumulated in a fixed order; the ordering (reverse
Cuthill-McKee with comparer-driven sorts of deterministic keys) is a function of the pattern; the factorisation
and `SolveInto` are scalar double-precision loops with no explicit fused multiply-add and no parallelism; `t` is
`step * dt` so no rounding accumulates in the time axis. The guarantees that follow:

- Same network, `dt`, rule, options and events: the same bits on every run on one machine and runtime, including after
  `Reset()` (`EmtCaseConsistencyTests`, which uses an AC source and compares two runs sample by sample in one
  process).
- A rebuilt, element-equal network gives the same bits as the original (`EmtCaseConsistencyTests`).
- A scheduled event that changes nothing leaves the trajectory bit-identical to the event-free run
  (`DistributionAndEmtInputValidationTests`).
- The five cases gb-full, uk, case9, case30 and case33bw have pinned trajectories: the 13 digests of
  `EmtUntappedBitIdentityTests` fail on any change of the last bit of any recorded sample (the sampled node voltages and
  the first 20 branch currents, over 40 ms at 20 us). Other cases (case24, case118 and the rest) are not pinned. The
  digest runs use a DC source. An AC source evaluates `DetMath.Cos` (the deterministic fdlibm cosine, the same bits on
  every machine), but no AC trajectory is pinned by a digest yet: AC runs are checked for run-to-run identity
  (`EmtCaseConsistencyTests`) and against analytic oracles with tolerances.
- The timing fields (`WallMs`, the printed milliseconds) are measurements and are not part of the result.

## Validation results

Each oracle is independent of the engine. Tolerances are the test assertions.

| Case | Setup | Result asserted |
|---|---|---|
| Series RLC step, underdamped, critical, overdamped | `V = 1` pu, `L = C = 1e-3`, `R = 0.4, 2.0, 4.0`, `dt = 1 us`, 3 ms | max `|i_emt - i_analytic| < 1e-5` |
| Order of accuracy | `R = 0.4`, 2 ms, `dt = 2 us` against `1 us` | error ratio `> 3.0` (second order gives 4) |
| Series R-C loop (same three regimes) | R and C in one `SeriesCapacitorBranch`, inductor to ground | max error `< 1e-5` |
| Series R-C order | `dt = 2 us` against `1 us` | trapezoidal ratio `> 3.0`; backward Euler ratio in `(1.8, 2.2)` and fine-step error `< 1e-2` |
| DC settle | `R = 1`, `L = 1e-3`, `Rload = 1`, 20 time constants | `i = 0.5` and `v1 = 0.5` to 6 decimals |
| Tapped arm at DC | `a = 1.1`, `R = 0.5`, `G = 2`, 50 time constants | `i = (V/a)/(R + 1/G)` and `v1 = i/G` to 12 decimals |
| Closing onto LC | `R = 0.01`, `L = C = 1e-3`, `dt = 2 us`, close at 1 ms, 8 ms | peak in `[1.9, 2.02]` pu; at least 3 refactorisations |
| CDA | capacitor switched onto a voltage step, `dt = 2 us` | peak second difference of the capacitor current with CDA `< 0.5 x` without |
| Matched line | `Rs = Zc = 100`, `tau = 1 ms`, `dt = 10 us` | far end below 0.02 before `tau`; `0.5` pu to 2 decimals at 2 ms and 2.9 ms; sending end `0.5` at 2 ms |
| Open-ended line | same source | far end below 0.05 before `tau`; `1.0` at 1.5 ms; sending end `0.5` at 0.5 ms and `1.0` at 2.5 ms (to 2 decimals) |
| Two-bus phasor, 8 parameter sets (tapped line with charging, tapped transformer, tapped and untapped series capacitors, negative-`R` equivalent, ideal series capacitor, tap 0) | 50 Hz, `dt = 10 us`, 1 s, one-bin DFT of the last cycle | `|V2_emt - V2_phasor| < 2e-6` pu against `AdmittanceStamp.Branch` |
| Negative charging, 3 parameter sets | same | `< 2e-6` pu |
| Power-flow equivalence, IEEE 14 and IEEE 57 | slack and PV buses ideal sources at their power-flow voltages, PQ injections as admittances, `dt = 10 us`, 0.5 s | worst bus `|V_emt - V_pf| < 2e-5` pu; the test asserts the bound; its comment records the measured 5.44e-6 pu (case14) and 5.35e-6 pu (case57) |
| Negative demand at DC | `R = 0.1`, `Pd = -50` MW on 100 MVA | `V2 = 1.05` pu within `1e-12` at every stepped sample |
| Negative demand at 50 Hz | `R = 0.1`, `X = 0.2`, `Gs = 40` MW | phasor error `< 2e-6` pu |
| Out-of-phase synchronisation | machine and inverter EMFs in opposition, breaker closes at a voltage zero, `dt = 1 us`, 60 ms | max `|i_emt - i_analytic| < 1e-5` pu after the close; first peak exceeds the AC magnitude |
| Three-phase balanced coupled line | `Rs = 1`, `Ls = 1 mH`, `Rm = 0.2`, `Lm = 0.3 mH`, `Rload = 10`, `dt = 20 us`, `t >= 0.08 s` | phase a against the positive-sequence phasor `< 5e-3`; `|va + vb + vc| < 5e-3` |
| Three-phase single-line-to-ground fault | `Rf = 0.01`, 40 ms to 90 ms | faulted phase rms `< 0.25 x` healthy phase B; phase C `> 0.5 x` pre-fault; phase a recovers to `> 0.7 x`; mean `|va+vb+vc| > 0.05` |
| Grid-following converter | `dt = 50 us`, `Rf = 0.05` fault 0.3 s to 0.45 s | `P = 0.5` to 2 decimals and `|V|` to 3 decimals at the phasor voltage; in the fault, `V` within 0.006 and `Iq`, `Ip` within 0.015 of an independent fixed point of the same law; recovers `P = 0.5` |
| Grid-forming converter | fault 3.0 s to 3.15 s | `P = 0.5` at 2 decimals, `|V|` within 0.01; `|I| = 1.2` pu to 2 decimals in the fault; recovers |

## Worked example

A bus fault on the IEEE 14-bus case, 50 Hz source at bus 1, branch current probed on 2-5. The fault closes at
10 ms through 0.01 pu and clears at 30 ms. The captures are in `docs/_captures/emt/`; timings in them are
measurements and differ by machine and run.

```
$ gridsim emt case14 --duration 0.05 --dt-us 20 --freq 50 --probe v:5,i:2-5 --event fault:5@0.01:0.01 --event clear:5@0.03
emt case14: 2,500 steps @ 20us, 5 factorization(s), peak |probe| 3.146, 15 ms -> results\emt-out
emt: 2 floating node(s) pinned at 0 V in the initial-condition solve.
[exit 0]
```

What the numbers are:

- `2,500 steps`: `round(0.05 / 20e-6)`. The matrix has 13 unknowns: 14 buses less the one Dirichlet node at bus 1.
- `2 floating node(s)`: buses 7 and 8 have no load and no shunt, and their arms (4-7, 7-8, 7-9) are all
  inductive, so they are open at rest and the buses are pinned at 0 V. Every other bus has a load conductance
  or a capacitor to ground. The verb reports only the count; the bus numbers come from the case data.
- `5 factorization(s)`: construction, plus two events times two refactorisations each. The fault at
  `0.01` applies at step 500 (`t = 500 * dt` is within `1e-6 * dt` of 0.01); the clear at step 1500.
- The fault switch has id 1000000, `Ron = 0.01` pu (`G = 100`), `Roff = 1e9` pu (`G = 1e-9`), a ratio of `1e11`
  between its two states, and it sits in the pattern at bus 5 from the first factorisation.

Companion values of branch 2-5 (`R = 0.05695`, `X = 0.17388`, `B = 0.0346`, no tap) at 50 Hz and `dt = 20 us`:

```
w0 = 314.159265359 rad/s       L = X/w0 = 5.53477230096e-4 s       C_end = (B/2)/w0 = 5.50676103098e-5 s
trapezoidal   2L/dt = 55.3477230096     Geq = 1/(R + 2L/dt) = 0.0180490190751     alpha = 0.997944216727
CDA half-step dt/2 = 10 us backward Euler   Geq = 1/(R + L/(dt/2)) = 0.0180490190751   I_hist = 0.998972108364 * i_prev
charging capacitor  trapezoidal Geq = 2*C_end/dt = 5.50676103098    I_hist = -Geq * v_prev - i_prev
```

The two conductances are the same number, as the refactorisation section states; the history coefficients are
what the burst changes.

The samples around the two events, CDA on (rows copied from `docs/_captures/emt/fault-bus5-case14-rows.txt`):

```
$ rows of emt-out/timeseries.csv (CDA on)
t_seconds,v:5,i:2-5
0,0,0
0.00996,-0.89793361,-0.328595161
0.00998,-0.89879204,-0.329316458
0.01,-0.0150150838,-0.360231654
0.01002,0.00481759349,-0.393051468
0.01004,-0.0139926538,-0.425672898
0.01006,-0.00313357157,-0.457848392
0.01008,-0.0148277463,-0.489623585
...
0.02998,-0.0341363571,-1.12263369
0.03,-0.540118702,-1.12662265
0.03002,-1.03093107,-1.11727015
0.03004,-1.4906826,-1.09091199
```

Reading it:

- The first row is the `t = 0` sample: node voltages read 0 before the first step.
- At 0.00998 s the bus-5 voltage is -0.8988 pu (the source is near its negative peak, `cos(2*pi*50*0.01) = -1`).
  At 0.01 s the fault has closed and the bus collapses to -0.0150 pu, while the branch current 2-5 rises smoothly
  (-0.3293, -0.3602, -0.3931, -0.4257): the inductors hold the current across the discontinuity, and the voltage
  is the only quantity that jumps.
- At the clear (0.03 s) the voltage recovers through -0.5401, -1.0309, -1.4907 pu with no chatter.

The same run with `--no-cda` (`docs/_captures/emt/fault-bus5-case14-no-cda.txt`: 3 factorisation(s), one
construction and one per event) shows what the burst removes:

```
$ rows of emt-out/timeseries.csv (--no-cda)
t_seconds,v:5,i:2-5
0,0,0
0.00996,-0.89793361,-0.328595161
0.00998,-0.89879204,-0.329316458
0.01,-0.107233042,-0.344319695
0.01002,0.0771907976,-0.376876996
0.01004,-0.067548886,-0.409976881
0.01006,0.0395528143,-0.442135543
0.01008,-0.0458202103,-0.47434145
```

Without CDA the bus voltage alternates sign every step, the Nyquist oscillation: the step-to-step swing between
0.01 s and 0.01002 s is `0.0771907976 - (-0.107233042) = 0.1844238396` pu. With CDA the same swing is
`0.00481759349 - (-0.0150150838) = 0.0198326773` pu, 9.3 times smaller, and the first post-fault sample is
-0.0150 pu instead of -0.1072 pu. CDA reduces the alternation and does not remove it: the successive swings with CDA are
0.0198, 0.0188, 0.0109 and 0.0117 pu over the next four steps, against 0.1844, 0.1447, 0.1071 and 0.0854 pu
without it.

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/Dynamics/Emt/EmtEngine.cs` | `EmtEngine` | Branch table, assembly, refactorisation, rest solve, time loop, events, CDA, case checks, `Reset` |
| `src/GridSim.Core/Dynamics/Emt/Companion.cs` | `Companion`, `EmtCompanionStamp` | Companion conductance and history coefficients for R, R-L, L, C, series R-C |
| `src/GridSim.Core/Dynamics/Emt/EmtElement.cs` | `EmtElement` and its records | `SeriesBranch`, `SeriesCapacitorBranch`, `ShuntElement`, `VoltageSource`, `CurrentSource`, `BergeronLine`, `SwitchElement` |
| `src/GridSim.Core/Dynamics/Emt/EmtNetwork.cs` | `EmtNetwork`, `EmtBuildOptions`, `EmtSourceSpec`, `EmtNegativeDemand`, `EmtCase` | Network container, `FromGridModel`, build refusals, the case record |
| `src/GridSim.Core/Dynamics/Emt/EmtEvent.cs` | `EmtEvent`, `SwitchEvent` | Scheduled switch operations |
| `src/GridSim.Core/Dynamics/Emt/EmtUnits.cs` | `EmtUnits` | Per-unit `L`, `C`, `Zc`, `tau`, `w0` |
| `src/GridSim.Core/Dynamics/Emt/EmtTypes.cs` | `IntegrationRule`, `EmtProbe`, `EmtPoint`, `EmtRunStats`, `EmtResult`, `EmtOptions`, `EmtConstants` | Rule, probes, results, options, named constants |
| `src/GridSim.Core/Dynamics/Emt/ThreePhaseEmt.cs` | `ThreePhaseEmtEngine`, `ThreePhaseNetwork`, `ThreePhaseBranch`, `ThreePhaseFault`, `ThreePhaseConverter`, `EmtConverterTrace` | Three-phase engine and average-value converters |
| `src/GridSim.Core/Numerics/Sparse.cs` | `CsrMatrix.Builder` | Pattern assembly, duplicate summing |
| `src/GridSim.Core/Numerics/SparseLu.cs` | `SparseLu` | Reverse Cuthill-McKee ordering, `FactorWithOrdering` |
| `src/GridSim.Core/Numerics/SparseLuFactorization.cs` | `SparseLuFactorization` | Gilbert-Peierls LU, `SolveInto` |
| `src/GridSim.Core/Numerics/NumericTolerances.cs` | `NumericTolerances` | Relative pivot floor |
| `src/GridSim.Cli/Program.cs` | `RunEmt` | The `emt` verb |
| `src/GridSim.Cli/CliDefaults.cs` | `CliDefaults` | Fault switch resistance and id base |
| `tests/GridSim.Tests/Emt*Tests.cs`, `ThreePhaseEmtTests.cs`, `DistributionAndEmtInputValidationTests.cs` | test classes | The invariants and validation above |

## Limits

- The `t = 0` sample shows the de-energised `t = 0-` state, not the `t = 0+` rest state; showing the rest state would move
  the trajectory digests pinned in `EmtUntappedBitIdentityTests` (N032, open for Mark).
- The three-phase engine has no builder from a `GridModel` and no CLI verb; it is reachable from code only.
- `ThreePhaseSource.FreqHz` defaults to 50 Hz regardless of the engine's `frequencyHz` (R138).
- Phase-shifting transformers are refused: a real-valued single-phase network has no element for a complex ratio.
- Bergeron lines are lossless and frequency-independent, and their delay is a whole number of steps, so the modelled
  travel time differs from `tau` by up to `dt/2` once `tau` is at least `dt/2`; a line with `tau` below `dt/2` is held at
  one step, an error of up to `dt - tau`.
- The scalar engine has no converter or machine models; the `CurrentSource` is the seam they would use.
- The `t = 0` sample reads zero for every node voltage; the rest solution seeds the histories but not the node-voltage array.
- A Bergeron line is outside the rest solve: its buffers start empty and it does not anchor a node.
- A series capacitor with `R = 0` is open in the rest solve, so an inductor in series with it starts from a zero-voltage seed.
- A negative series resistance or shunt conductance from the case data is accepted by the engine (and by the verb with `--allow-non-passive`); such a network may grow without bound.
- `gridsim emt` offers one ideal source, a DC or 0-phase sinusoid, `NegativeDemand = ConstantCurrent`, and no switch other than fault switches.
- A second fault switch at a bus exists, open, from `t = 0`, so a two-fault run agrees with the one-fault run before the second fault to `1e-6` pu, not bit for bit.
- The time step is fixed for the whole run; there is no adaptive stepping and no CDA for discontinuities other than switch events.

## See also

- [Ch. 16 (user manual): Electromagnetic transients](<../manual/0016 Electromagnetic transients (EMT).md>)
- [CLI reference, `emt`](<../manual/0019 CLI reference.md#emt>)
- [Design record: EMT time-domain solver](design/emt-time-domain.md)
- [Ch. 2: Data model and per-unit](<0002 Data model and per-unit conventions.md>)
- [Ch. 3: Power flow](<0003 Power flow.md>), the branch stamp the EMT build reproduces
- [Ch. 4: Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>)
- [Ch. 5: Frequency dynamics](<0005 Frequency dynamics.md>)
- [Ch. 19: Harmonics](<0019 Harmonics.md>)
- [Ch. 24: Standard dynamic model library](<0024 Standard dynamic-model library.md>), the converter control law
