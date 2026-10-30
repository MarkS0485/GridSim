# Starter maps for grids beyond GB

Ready-made GridSim network maps for grids other than Great Britain, so getting
started doesn't mean hand-authoring a network. Each folder is a standard GridSim
case dir (`system/buses/generation/demand/transmission/transformer` JSON) - load
it exactly like `gb-full` or an IEEE case:

```bash
gridsim physics-check data/intl/ireland-all-island   # solve + physics-law audit
gridsim --dir data/intl/ireland-all-island           # open in the simulator
```

| folder | grid | buses | notes |
|--------|------|------:|-------|
| `ireland-all-island` | Ireland (EirGrid + SONI) | 60 | converges; `physics-check` gate PASS (conservation laws only); **outside the voltage band**: 46 of 60 buses above 1.06 pu, highest 1.1258 pu (below) |
| `iberia` | Spain + Portugal | 1,497 | transmission backbone; **no AC power-flow solution as shipped** (below) |
| `france` | France (RTE) | 1,976 | transmission backbone; **no AC power-flow solution as shipped** (below) |
| `continental-europe` | CE synchronous area | 5,824 | larger than gb-full; **no AC power-flow solution as shipped** (below) |

Built by GDA from open, published transmission topology (PyPSA-Eur / GridKit
ENTSO-E extraction) via `NetworkModel/international`. Line impedances are
synthesised per voltage class and demand is a synthetic snapshot - the topology
and geography are real, but these are indicative maps, not planning-grade
models. See each folder's `MODEL_CARD.md` and `ATTRIBUTION.txt`.

Licence: (c) OpenStreetMap contributors, ODbL + PyPSA-Eur CC-BY-4.0.

## What passes for ireland-all-island, and what does not (N170, October 2026)

This row used to say only "converges, physics gate PASS", which read as a clean bill of health. Exactly:

- **Passes: the base power flow converges.** 5 iterations, largest mismatch 6.45E-13 pu, losses 12.512 MW and
  -739.481 MVAr (the network generates reactive power: line charging exceeds the reactive losses).
- **Passes: the `physics-check` gate.** The gate is a physics-LAW audit of that converged state, nothing more:
  conservation of energy (residual 2.92E-12 MW), Ohm's law (1.26E-11 MVA), Joule heating (2.93E-12 MW), Kirchhoff's
  current law (1.50E-11 MW) and S = V I* (2.71E-11 MVA) hold, every branch is passive, and Kirchhoff's voltage law
  holds by construction. `GATE: PASS - state is physically admissible (all hard laws hold)` means the solution is a
  self-consistent solution of the network equations. It checks no operating limit.
- **Does not pass: the voltage band.** The solved voltages run from 1.0199 pu (bus 27) to 1.1258 pu (bus 9); 46 of
  the 60 buses are above GridSim's 1.06 pu screening limit (the 0.94-1.06 pu band of `nminus1` and `--once`) and 45
  are above 1.10 pu (41 of the 56 220 kV buses and all four 380 kV buses). `gridsim --dir
  data/intl/ireland-all-island --once` reports `Voltage range: 1.020 - 1.126 p.u. (OUT OF BAND)`, and `nminus1`
  reports it as a pre-existing base-case overvoltage (exit 3). The cause is the one described for the larger maps
  below: 50 of the 51 units are fixed (`"role": "pq"`) injections, so only the slack holds a voltage against the
  line charging; the network is just small enough to solve.

So use it as a solvable topology map, not as a secure operating point. Bringing its voltages into band needs the same
data work in GDA's builder as the other maps (voltage-controlling units or reactive compensation).

## Which maps solve (N113, October 2026)

Only `ireland-all-island` has an AC power-flow solution (5 iterations; highest voltage 1.1258 pu, at bus 9, from
line charging; outside the voltage band, see above). `iberia`, `france` and `continental-europe` do not converge in a base solve, with reactive limits on
or off, from the case's voltages or a flat start (100 iterations: iberia stops at a 0.343 pu mismatch with the lowest
voltage 0.283 pu; france at 3.495 pu with the highest voltage 3.151 pu; continental-europe at 26.7 pu with 0.257 and
1.869 pu). GridSim loads them and prints a note saying so; any verb that solves them reports the non-convergence
(exit 3). This is the data, not the solver:

- **Nothing holds the voltage but one bus per island.** Every unit except the island slacks has `"role": "pq"` (a
  fixed injection with no voltage control): 225 of 227 units in iberia, 293 of 295 in france, 1,604 of 1,608 in
  continental-europe (ireland: 50 of 51, but its 60 buses are small enough to solve). So the line charging of the
  whole country (22,811 MVAr at 1 pu in iberia, 34,473 MVAr in france, 140,717 MVAr in continental-europe; no
  shunt reactors are modelled) can only be absorbed at the slack. Iberia does not solve even with no load and no
  generation: at zero load the largest fraction of its line charging that solves is 25.4 % (france 13.2 %), with
  voltages already at 2.29 pu (france 2.37 pu) far from the slack. With charging removed the no-load networks solve
  in 3 iterations.
- **The impedances are realistic.** The synthesised per-km values are ordinary overhead-line figures (220 kV: 0.32
  ohm/km and 3.6 uS/km; 380 kV: 0.285 ohm/km and 4.3 uS/km, measured against the straight-line distance between
  the bus coordinates, which the line lengths exceed by about 4 % at the median), so the charging is what lines of
  those lengths carry.
- **Generation is far from the synthetic demand.** In france and continental-europe the DC power flow of the stored
  dispatch already needs angles across single lines of 58.8 deg (france) and 110.7 deg (continental-europe, 11,367
  MW on a 2,000 MVA line), beyond what an AC line can carry; continental-europe's second island has 13,000.9 MW of
  demand and 812.0 MW of generation. Turning every unit into a voltage-controlling (PV) unit with reactive limits off
  makes iberia solve (4 iterations, voltages 1.0132 to 1.0700 pu) but not france (stops at 20.8 pu) or
  continental-europe (152 pu).

Making them solvable needs data work in GDA's international builder (voltage-controlling units, reactive
compensation, a dispatch near the demand), not a solver change; they are kept as maps of the topology.
