# Network equivalents

GridSim builds three static network equivalents from a solved base case: the Ward equivalent, the extended Ward
equivalent and the REI equivalent. The buses the user keeps stay as they are; every other bus (the external network) is
replaced by a few elements at the boundary. The reduced case is an ordinary GridSim case, so every study runs on it. The
code is in `src/GridSim.Core/PowerFlow/Equivalents/` (`NetworkReduction`, `WardMath`), the verb is `gridsim reduce`
([user manual ch. 19](<../manual/0019 CLI reference.md#reduce>)), and the tests are
`tests/GridSim.Tests/Rc5Wp9NetworkReductionTests.cs`.

The feature is additive. It is a new namespace and a new verb, and it calls `SparseKronReduction` and the branch stamps
unchanged. No existing study output changes.

Two more equivalents sit beside them: the short-circuit equivalent (`reduce --method sc`), which reduces the IEC 60909
sequence networks, and the dynamic equivalent (`reduce --method dynamic`), which aggregates coherent machines into
classical machines. Both are described after the Verification section below; their tests are
`tests/GridSim.Tests/Rc5Wp31EquivalentsTests.cs`.

## Partition

The user names the kept set K. GridSim computes the boundary B, the kept buses with an in-service branch to an external
bus, in model order. E is every other bus. All quantities are per unit on the system base.

The base case must be solved and converged, with the slack bus in K. The CLI solves it with reactive limits off, so the
PV buses hold their set-points; the equivalence is stated for that condition.

## Ward equivalent

Ward ("Equivalent circuits for power-flow studies", AIEE Trans. 68, 1949); Monticelli (*State Estimation in Electric
Power Systems*, Kluwer 1999, ch. 12).

Y^x is built on B and E from the tie branches, the external branches and the external bus shunts only, with the same
stamps and accumulation order as the power-flow Y-bus. The internal and boundary branches stay in the reduced case as
they are. The equivalent admittance is the Kron reduction of Y^x onto B:

    Y_eq = Y^x_BB - Y^x_BE (Y^x_EE)^-1 Y^x_EB

computed by `SparseKronReduction.Reduce`, which eliminates E in ascending index order.

The external current injections come from the solved state, I_E = (Y V)_E, the net injection of every generator and load
at the external buses. They move to the boundary as

    dI_B = -Y^x_BE (Y^x_EE)^-1 I_E,    dS_b = V_b conj(dI_b)

`WardMath.TransferredCurrents` gets dI_B from the same Kron code. It borders the matrix with one extra kept column that
holds I_E, with a unit diagonal and an otherwise empty row. Its Schur complement holds dI_B in that column. The empty row
never updates another row, so the kept block is the plain reduction's value for value; a test checks this bit for bit.

**Why the base case is exact.** The KCL rows of E give V_E = (Y^x_EE)^-1 (I_E - Y^x_EB V_B). Substituted into the rows
of B, they give

    I_B + dI_B = (Y^int V)_B + Y_eq V_B

Every reduced equation is an original equation rearranged. So the base-case voltages of K solve the reduced case up to
round-off, whatever the Newton tolerance of the base solve. The tests check the reduced residual at the full model's
voltages against the full residual. They differ by less than 1e-13 pu on case14.

**Representation.** The reduced case holds:

- An equivalent branch "EQ:i-j" for each boundary pair with Y_eq,ij != 0. Its series admittance is
  y = -(Y_eq,ij + Y_eq,ji)/2, with R + jX = 1/y, no charging and tap 1. R or X may be negative.
- A shunt s_b = sum_j Y_eq,bj (the row sum) at each boundary bus, added as Gs += Re s_b Sbase and Bs += Im s_b Sbase.
- The injection dS_b at each boundary bus, taken off the load: Pd -= Re dS_b Sbase and Qd -= Im dS_b Sbase. It is
  constant power.
- Only an exact zero drops a branch. A small value is kept, because dropping it would break exactness.

The equivalent is refused when |Y_eq,ij - Y_eq,ji| > 1e-9 |Y_eq,ij|. A phase shifter in E makes Y_eq non-reciprocal, and
that is out of scope. The external generators, loads and branches are removed, and the notes give their counts. The kept
buses carry the base-case voltages as their start values.

## Extended Ward equivalent

Deckmann, Pizzolante, Monticelli, Stott and Alsac ("Studies on power system load flow equivalencing", IEEE Trans. PAS-99(6),
1980); the pandapower `xward` element.

The extended Ward is the Ward equivalent plus one reactive-support path per boundary bus that stands in for the external
PV buses. Y_g is the same reduction as Y_eq, but with the external PV buses grounded: removed from E, held at their
voltage. Then

    d_b = sum_j (Y_g - Y_eq)_bj

For each b with d_b != 0, GridSim adds a fictitious bus "XW:b" (id max(id) + 1, + 2, ... in boundary order). It is a PV
bus with the base-case |V_b| as its set-point, P = 0 and unlimited Q, through a power-flow-only generator. It is joined to
b by a branch of admittance d_b. At the base case both ends are at V_b, so that branch carries no current and the base case
stays exact. Under a change the source stands in for the voltage support of the external PV buses. Its Q is unlimited,
so it does not model their reactive limits.

**GridSim uses the full complex Y here, not the B'' matrix of the 1980 paper.** The paper builds the support admittances
from the fast-decoupled B'' matrix (susceptances only, resistances ignored). GridSim reduces the same complex Y^x as the
Ward part, grounding the PV buses, so d_b is complex and can carry a small conductance of either sign. This is GridSim's
own choice. The figures below are for this form, not the paper's.

## REI equivalent

Dimo (*Nodal Analysis of Power Systems*, Abacus 1975); Tinney and Powell ("The REI approach to power network
equivalents", PICA 1977); Savulescu ("Equivalents for security analysis of power systems", IEEE PAS-100(5), 1981).

The external injections of a group move to one fictitious bus R through a zero-power-balance (ZPB) network. The default
group is every in-service external generator. With `--rei-groups gen,load` the external loads form a second group with
their own bus.

- S_k is the injection of the group's elements at bus k. For a generator this is the solved net injection plus the bus
  load. Then I_k = conj(S_k / V_k), S_R = sum S_k, I_R = sum I_k and V_R = S_R / conj(I_R).
- The branch from k to the ZPB node 0 has admittance y_k = -conj(S_k)/|V_k|^2. The branch from 0 to R has
  y_R = conj(S_R)/|V_R|^2.
- The KCL of node 0 gives V_0 = (sum y_k V_k + y_R V_R)/(sum y_k + y_R) = (-sum I_k + I_R)/(...) = 0. The branch k-0
  delivers exactly I_k into bus k, so the original injections are reproduced and the ZPB network absorbs no net power.
- The group is refused when |I_R| < 1e-9 pu (no current to aggregate) or |sum y_k + y_R| < 1e-12 pu.

Then the Ward equivalent is built on the extended network, with E' = E plus node 0 and B' = B plus R. The generator
group's R is a PV bus: its set-point is |V_R|, P = Re S_R, and its Q limits are the sum of the group's limits (unlimited
when any is). Its base-case angle is arg V_R; it is not the slack. The load group's R is a PQ bus that draws the aggregated
load. External elements outside any group (the loads, with the default grouping) become Ward injections.

The denominator of V_0 can nearly cancel. On case14, y_k = jQ_k/|V_k|^2 for the two condensers and y_R = -jQ_R/|V_R|^2
sum to 3.6e-5 pu, against terms of 0.1 to 0.3 pu. V_0 computed in floating point is then about 3e-12, not 1e-16.

The test bounds it by a constant counted from the operations, with u = 2^-53 the unit roundoff and
T = sum|y_k V_k| + |y_R V_R|:

- Each step's error is taken from the standard model. A complex product xy is within 2 sqrt(2) u |x||y| of exact (Higham,
  *Accuracy and Stability of Numerical Algorithms*, Lemma 3.5). |z|^2 followed by a division into each component is
  within 3u. .NET's complex division (Smith's algorithm) is within 7u, counted the same way.
- In exact arithmetic y_k V_k = -I_k and y_R V_R = I_R, where I_R is the computed sum. So the rounding of S_R and of
  I_R cancels out of the numerator.
- What is left: each y_k V_k term is within (3 + 2 sqrt(2))u |I_k|. Each quotient S_k/V_k inside I_R is within 7u |I_k|,
  and the n - 1 additions of I_R add (n - 1)u sum|I_k|. The y_R V_R term is within (7 + 3 + 2 sqrt(2))u |I_R|. The n
  additions of the numerator add n u T.
- Together, |num| <= (n + max(9 + n + 2 sqrt(2), 10 + 2 sqrt(2))) u T, to first order in u. For n = 2 this is 15.83 u T.

So |V_0| <= 15.83 u T / |sum y|, which is 2.7e-11 on case14. NumPy computes |V_0| = 3.1e-12 there. The reduction itself
is not affected: the reduced case still meets the base case to 1e-12.

## Verification

The tests use `data/matpower/lib/case14.m` with reactive limits off, keeping buses 1 to 5. The tie transformers 4-7, 4-9
and 5-6 make B = {4, 5}; E = {6, ..., 14} holds the synchronous condensers at buses 6 and 8. Every figure was recomputed
with an independent NumPy implementation (a dense MATPOWER-model Y-bus, `numpy.linalg.solve` for the Schur complements and
its own Newton to 1e-14 pu) and with the exactness algebra.

| Check | Ward | Extended Ward | REI |
|---|---|---|---|
| Reduced residual at the full voltages, PQ and PV buses | <= 1e-12 pu | <= 1e-12 pu | <= 1e-12 pu |
| Reduced case from a flat start: max \|V_red - V_full\|, buses 1 to 5 | <= 1e-12 pu | <= 1e-12 pu | <= 1e-12 pu |

Also pinned:

- Y_eq to 1e-12 and dS_4 = -48.47031257646785 - j11.762167926061588 MVA (to 1e-9).
- d_4 = 0.25452509094986836 - j4.120194623205634 and d_5 = -0.0014383397131226272 - j4.559892356534604.
- S_R = j30.354395775362498 Mvar, |V_R| = 1.0815513178991354, arg V_R = -13.724762010059429 deg.
- A three-bus hand case. Its Y_eq is exactly zero and its boundary injection is the load plus the tie's reactive loss,
  dS_2 = S_3 - jX|I_3|^2, to 1e-13.
- Internal branch flows and the slack P and Q agree with the full model to 1e-9 MVA.
- A case written by `reduce --out x.m` and solved by `pf` gives the boundary voltages to 1e-10.
- The written case is byte-identical across runs and cultures.

**Perturbation error.** The loads at buses 3 and 4 are scaled by 1.2 in P and Q, the same change on the full and the
reduced case, with the equivalent left frozen. The table gives the largest boundary voltage-magnitude error,
| |V_red| - |V_full| |, and the largest flow error on an internal branch (either end).

| Method | Boundary \|V\| error (pu) | Internal flow error (MVA), branch |
|---|---|---|
| Ward | 6.329073012851794e-4 | 0.36739575085202064 (3-4) |
| Extended Ward | 3.942355790220731e-5 | 0.07973906095373332 (4-5) |
| REI | 6.969321457273026e-5 | 0.05701400270564336 (4-5) |

The full model's boundary |V| goes from 1.017670853691765 and 1.0195138598190605 to 1.0160091460480294 and
1.0180274127640931. A Ward equivalent holds constant-power boundary injections and gives no external voltage support, so
its error grows with the internal change: 6e-4 pu here for a 20 % change at two buses. The extended Ward and REI forms
cut it by 9 to 16 times. These figures are exact for this case and this change only. The error of an equivalent depends
on how far the operating point moves from the base case, and no general bound is claimed.

## Short-circuit equivalent

`reduce --method sc` (`ShortCircuitEquivalent`) reduces the sequence networks of the IEC 60909 fault studies instead of
the power-flow network, so the fault studies on the reduced case give the full case's currents at the kept buses. The
partition is the one above. No power flow is needed.

**The sequence networks.** They are built by the fault studies' own code, on a sub-case holding B, E, the tie and
external branches and the external machines:

- positive sequence: the IEC 60909 series network (line charging and bus shunts removed, taps kept) plus each external
  synchronous machine as 1 / j(X''d + X_T) on the system base;
- negative sequence: the same network with each machine's X2. A GridSim case carries one positive- and
  negative-sequence impedance per branch, so the reduction is refused when an external machine's X2 differs from its
  X''d. Otherwise Y_2 = Y_1 exactly;
- zero sequence: the network the earth-fault study builds (line Z0, transformer winding connections and neutral
  reactances, machine zero-sequence impedances), with every mutual coupling between external or tie lines stamped from
  the inverse of its group's impedance matrix. A mutual inside the external network is therefore captured exactly.
  External buses with no zero-sequence path to the boundary take no part.

Each is Kron-reduced onto B with `SparseKronReduction` (the elimination order of the Ward equivalent):
Y_s,eq = Y_s,BB - Y_s,BE (Y_s,EE)^-1 Y_s,EB.

**How the reduced matrices are written.** For each boundary pair, a branch "SC:i-j" with the positive-sequence series
admittance y = -(Y_ij + Y_ji) / 2 and the zero-sequence impedance R0 + jX0 = 1 / y0. A pair with no zero-sequence
coupling (y0 exactly 0) gets delta/delta windings, which block the zero sequence. The IEC 60909 positive and negative
sequences neglect bus shunts, so the positive-sequence row sum s of each boundary bus is carried as a source: a
fictitious bus "SC:b" joined to b by a branch R + jX = Re(1/s) + j Im(1/s)/2, with a synchronous machine of X''d =
Im(1/s)/2 on a machine base equal to the system base. The series path is exactly 1/s. In IEC 60909 terms this is the
equivalent voltage source c Un / sqrt(3) behind the Norton admittance of the eliminated machines. The same branch carries
the zero-sequence row sum s0 as a grounded-wye (at b) / delta transformer whose zero-sequence impedance is 1/s0, a shunt
to earth of exactly s0 at b. Row sums that are zero in exact arithmetic are left out rather than written from rounding:
the positive-sequence one when the external network holds no machine and no off-nominal tap, the zero-sequence one when
the reduced zero-sequence network has no path to earth.

The verb also prints the Thevenin impedance of the external network at each boundary bus and sequence, the diagonal of
(Y_s,eq)^-1, and a check: the IEC 60909 three-phase Ik'' at each boundary bus with a base kV, on the full and the reduced
case.

**What it is exact for.** The driving-point and transfer impedances among the kept buses are those of the full network
in every sequence, so the initial symmetrical currents Ik'' of three-phase, line-to-line, single- and double-line-to-earth
faults at the kept buses are the full model's, to rounding. The peak factor's meshed-network flag, the near-to-generator
breaking factors and Method C read the network's topology and individual machines, which the equivalent replaces, so ip
and Ib can differ. Loads are not represented (IEC 60909 neglects them), and the reduced case's power flow is not the base
case; for that, use the Ward family. Apply the IEC 60909 correction factors K_G and K_T to the full case before reducing
it: applied to the reduced case, they would also correct the fictitious "SC:" machines and the equivalent branches, and
the currents would no longer be the full case's.

**Thevenin report.** The diagonal of (Y_s,eq)^-1 is taken per boundary bus: a bus whose row and column of Y_s,eq are
zero (the external network gives it no path in that sequence, for example a boundary bus whose only tie is the delta side
of a Dyn transformer, in the zero sequence) is reported infinite, and the others come from the inverse of the remaining
block.

**Written out.** `--out case.json` writes a JSON v2 case through `V2CaseWriter.WriteWithFaultSequence`, which adds
what the elements alone cannot hold: the winding connections of the equivalent branches and of any kept transformer
written in place as a line (`lineWindings`), the machine zero-sequence impedances (`machineZeroSequence`) and the kept
mutual couplings (`zeroSequenceMutuals`); see [ch. 28](<0028 The v2 JSON schema.md>). Every number is written in its
shortest round-trip form and every branch keeps its place, so the loaded-back case gives the same fault currents, bit for
bit. The verb reads the written case back and prints that check (`reload check`: the four fault types at every kept bus
with a base kV). MATPOWER holds no X''d and no sequence data, so even the three-phase current of a written `.m` would
differ (the fictitious sources would reload with the default X''d); `--method sc --out x.m` is refused by name.

**Refused**, naming the element: a converter-interfaced unit in E (its IEC 60909 infeed is a current source weighted by
|Z_kj|, which a Kron reduction does not carry), an external machine whose X2 differs from its X''d, a mutual coupling
between a kept line and an external or tie line, a phase shifter in E, a zero-sequence coupling between two boundary
buses with no positive-sequence coupling, a positive-sequence row sum that is not inductive, every zero-sequence network
the earth-fault study refuses, and everything the Ward equivalent refuses in E except ZIP loads.

**Verification** (`tests/GridSim.Tests/Rc5Wp31EquivalentsTests.cs`). case14 with base kV 132, the transformers 4-7 dYN,
4-9 YNyn, 5-6 YNd and 7-9 YNyn, machine zero-sequence impedances at buses 1, 2 and 6, kept set {1, 2, 3, 4, 5, 7, 8},
so E = {6, 9, ..., 14} and B = {4, 5, 7}. The three-phase, line-to-line and single-line-to-earth Ik'' at every kept bus
match the full case to 1e-12 relative, with and without a zero-sequence mutual between the external lines 6-12 and 6-13,
and so do the double-line-to-earth currents. Written and read back (`tests/GridSim.Tests/Rc5Wp31ScWriterTests.cs`), the
equivalent gives the same four currents at every kept bus, bit for bit.
The tolerance rests on an estimate, n kappa u per backward-stable step with no growth-factor term (kappa(Y) = 59 and 36 for the positive and zero sequences, n = 14), below
3e-13; an independent NumPy implementation of the same sequence networks observed 5.3e-16, and its full-case boundary
currents match GridSim's to the same bound (bus 4: 13.291629317238353, 11.510888646414427 and 10.724597757175335 pu).
The mutual moves the bus 7 earth-fault current by 1.1e-4 relative, so that check is not vacuous.

## Dynamic equivalent

`reduce --method dynamic` aggregates groups of coherent machines into one classical machine each
(`GridSim.Core.Dynamics.Equivalents`: `CoherencyGrouping`, `InertialAggregation`). Every bus is kept; the machines move.

**Groups.** The user gives them (`--groups`, one group per line of bus ids), or the slow-coherency method of Chow finds r
of them (`--coherency r`; J. H. Chow, ed., *Time-Scale Modeling of Dynamic Networks with Applications to Power Systems*,
Springer 1982; the grouping algorithm of Avramovic, Kokotovic, Winkelman and Chow, Automatica 16, 1980, as summarised by
Chow, Galarza, Accari and Price, IEEE Trans. Power Systems 10(2), 1995). The swing model is linearised at the operating
point as the classical small-signal assembler does it (loads as constant admittances, each machine behind X'd, Kron
reduction to the internal nodes): M d2(delta)/dt2 = -omega_s K delta with M = diag(2H) and K the synchronising matrix.
Transfer conductances make K unsymmetric (on Kundur's two-area system by 13 % of the largest |K_ij|); the grouping uses
its symmetric part K_s. The steps:

1. The r slowest modes: the eigenvectors of M^-1/2 K_s M^-1/2 (cyclic Jacobi) for the r smallest eigenvalues, mapped
   back as V = M^-1/2 U. The first is the rigid centre-of-inertia mode. K's rows sum to zero but K_s's need not, so its
   eigenvalue is near zero rather than zero (-0.0022 on Kundur's system).
2. Gaussian elimination with complete pivoting on V picks one reference machine per slow mode.
3. L = V V1^-1, with V1 the reference machines' rows.
4. Each machine joins the reference with the largest entry in its row of L.

**Aggregation** (A. J. Germond and R. Podmore, "Dynamic aggregation of generating unit models", IEEE Trans. PAS-97(4),
1978, the inertial method). The machines of a group move to one bus q at V_eq = sum S_i V_i / sum S_i, each terminal bus
t joined to q by an ideal transformer of ratio a_t = V_t / V_eq. An ideal transformer has no impedance, so it cannot be
written as a branch; it is absorbed into the network exactly. Every branch at t is moved to q with the transformer in its
own stamp: at the from end the complex tap becomes T / a_t; at the to end it becomes T a_t, with the series impedance
divided and the charging multiplied by |a_t|^2; a branch between two terminal buses of the same group becomes a shunt at
q, conj(a_f)(Y_ff a_f + Y_ft a_t) + conj(a_t)(Y_tf a_f + Y_tt a_t). Loads move to q unchanged (the transformers are
lossless) and bus shunts scale by |a_t|^2. The current into q is then the sum of conj(a_t) times the currents at the
terminal buses, which is the power balance of the ideal transformers, so the solved base case solves the reduced case and
the power flow is preserved exactly. A group whose machines share one bus keeps that bus and the network is unchanged.

The equivalent machine is classical (GENCLS) on S_eq = sum S_i: H_eq = sum H_i S_i / S_eq, D_eq = sum D_i S_i / S_eq, and
X'_eq = S_eq / sum(|a_i|^2 S_i / X'_i), the parallel combination of the members' X'd referred to q (with |a_i| = 1 it is
(sum 1/X'_i on the S_eq base)^-1). Its output is the members' solved output and its voltage set-point |V_eq|. With
`--out`, the verb writes the reduced case and, beside it, a `gridsim-dynamics/2` document with one GENCLS template per
machine. The CLI gives every machine the X'd of `--xdp` and no damping, so it tags that data Synthetic; the Core entry
point takes per-machine X'd and damping.

**What it is and is not.** When the members move together, as identical machines at one bus do, the equivalent is exact.
Otherwise it is an approximation whose quality follows the coherency. Exciters, governors, stabilisers and converters are
not aggregated, and there is no frequency-dependent equivalent for EMT.

**Refused**, naming the element: a member that is not a synchronous machine or has no inertia, a generator in two
groups, a generator outside the group at a group's terminal bus, and, at a merged terminal bus, a ZIP load, HVDC, FACTS,
SVC, induction machine, OLTC, flow control, boundary or zero-sequence mutual coupling.

**Verification** (`tests/GridSim.Tests/Rc5Wp31EquivalentsTests.cs`, with an independent NumPy implementation of the power
flow, the classical linearisation, the slow-coherency steps and the merge):

- Two identical machines at one bus (H 3 s, X'd 0.3 on 100 MVA) and their aggregate (H 3 s, X'd 0.3 on 200 MVA, the
  parallel of the two) follow the same rotor angle, speed and group electrical power at every step of a 1 s run with a
  three-phase fault at a remote bus, to 1e-12 relative. The aggregate is exact here; the bound covers rounding (moving the
  aggregate's data by one ulp moves the trajectories by about 1e-15 in the NumPy run).
- Machines of (100 MVA, 3 s) and (300 MVA, 5 s) aggregate to S_eq = 400 MVA and H_eq = 4.5 s exactly.
- Kundur's two-area system (*Power System Stability and Control*, Example 12.6, classical machines with X'd 0.3): slow
  coherency with r = 2 gives {G1, G2} and {G3, G4}. The merged equivalent, solved from a flat start, meets the full
  voltages to 1e-9 pu. Its inter-area mode is 0.549164910 Hz against 0.530596340 Hz for the full system (both from the
  classical small-signal assembler, both matching the NumPy figures to 1e-6): 3.5 % apart, within the 5 % bound the test
  holds. The 5 % is an observed bound for this case, not a derived one.

The equivalents have not been compared with PSS/E or PowerFactory equivalents, which needs a vendor licence.

## Scope

Covered: the Ward, extended Ward and REI equivalents of a balanced positive-sequence network with constant-power external
injections, at a converged base case with reactive limits off.

Not covered by the Ward, extended Ward and REI equivalents:

- Dynamic equivalents (coherency-based aggregation of machines): `reduce --method dynamic` builds those, classical
  machines only (see above).
- Short-circuit equivalents. The sequence networks are not reduced and the equivalent elements carry no zero- or
  negative-sequence data. A fault study on a Ward, extended Ward or REI case does not represent the external network;
  `reduce --method sc` builds the short-circuit equivalent (see above).
- Optimal boundary selection and multi-area REI grouping by electrical distance.
- Phase shifters, HVDC, FACTS, SVCs, induction machines, OLTCs and flow controls in the external network; each is refused
  by name.
- Voltage-dependent (ZIP) loads in the external network (refused), the reactive limits of the external machines (the XW
  and REI sources are unlimited or summed), and PV/PQ switching inside the external network.
- Authored asset dynamics and protections are not carried to the reduced case.

The equivalents are verified against the independent NumPy implementation above and by the exactness algebra. They have
not been compared with PSS/E or PowerFactory equivalents.
