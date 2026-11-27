# Geomagnetically induced currents (GIC)

GridSim computes geomagnetically induced currents for a uniform geoelectric field by the DC network method of the NERC
*Application Guide: Computing Geomagnetically-Induced Current in the Bulk-Power System* (December 2013). The code is in
`src/GridSim.Core/Analysis/Gic/` (`GicFile`, `GicDistances`, `GicNetwork`, `GicSolver`, `GicResult`), the verb is
`gridsim gic` ([user manual ch. 19](<../manual/0019 CLI reference.md#gic>)), and the tests are
`tests/GridSim.Tests/Rc5Wp28GicTests.cs`.

The verb and the Core types are new. No existing output changes.

## Data: the `gridsim-gic/1` file

A load-flow case does not hold what a GIC study needs, so it comes from a sidecar file:

```json
{ "schema": "gridsim-gic/1", "distanceModel": "wgs84",
  "substations": [ { "name": "S1", "lat": 33.6135, "lon": -87.3737, "groundOhms": 0.2, "buses": [1, 2] } ],
  "transformers": [ { "branch": "1-2", "type": "gsu", "hvWindingOhms": 0.5, "kMvarPerA": 1.18, "blocked": false } ],
  "lineOhmsOverride": { "3-4": 3.525 } }
```

- `substations`: name, latitude and longitude (degrees), the ground grid resistance (ohms, the whole grid) and the buses
  inside the substation. A bus belongs to at most one substation.
- `transformers`: the branch, by its object-model key (the branch name, or `from-to` with `#n` on a repeat), and its type:
  `gsu` (or `gy-d`: grounded-wye HV, delta LV), `gy-gy`, `auto`, `d-gy` or `open` (no DC path). Winding resistances are
  DC ohms per phase; for an autotransformer `hvWindingOhms` is the series winding and `lvWindingOhms` the common
  winding. `kMvarPerA` is the reactive-loss factor; `blocked` opens the neutral (a GIC blocking device).
- `lineOhmsOverride`: the DC resistance per phase of a line, by branch key. Without it a line's resistance is its R times
  the impedance base of its from bus (kV^2 / MVA).
- `distanceModel`: `wgs84` (the default) or `spherical`, below.

The reader is strict: an unknown or repeated key, a missing required key, a bus in two substations, a resistance of 0 or
less and an unknown type are refused by name. Every in-service transformer of the case must be described, and every line
must have both buses in substations, or the network build refuses it by name. The HV side of a transformer is the bus
with the higher base voltage, so both base voltages must be set and differ. The file carries no timestamp: the field is a
command-line input, so no forecast horizon applies.

## Distances

A line from substation a to substation b has the path vector (L_N, L_E) from a to b, in km. With phi the mean latitude:

- `wgs84`, the guide's eqs. A.3 and A.7 (WGS84 ellipsoid): L_N = (111.133 - 0.56 cos 2phi) dlat,
  L_E = (111.5065 - 0.1872 cos 2phi) cos phi dlon.
- `spherical`: L_N = 111.2 dlat, L_E = 111.2 cos phi dlon.

The guide's own worked example (Appendix II, Table B-4) does not follow its eqs. A.3 and A.7: for line 1 they give
77.306 km north, the table 77.499 km. The table's distances, and the currents of Tables B-6 and B-7, follow a sphere at
about 111.2 km per degree. GridSim offers both, and uses the guide's formulas unless the file asks for `spherical`.

## The DC network

The field E = (E_N, E_E), V/km, gives each line the source V = E_N L_N + E_E L_E, which drives current from its from bus
to its to bus. The elements, each the three phases in parallel:

- a line: R/3 between its two bus nodes, with its source;
- a grounded-wye winding: R/3 from its bus to the substation neutral;
- an autotransformer: the series winding R_s/3 from the H bus to the X bus, and the common winding R_c/3 from the X bus to
  the neutral;
- a delta winding: open;
- each substation: its ground grid R_g from the neutral to remote earth.

A blocked transformer has the winding-to-neutral path removed (for an autotransformer, the common winding). The nodes are
numbered in a fixed order: the substation neutrals in file order, then the buses any element touches, in case order.
The sources become Norton pairs (g V out of the from node, into the to node), and Y V = J is solved with the sparse LU.

Two steps make the solve exact where the physics is exact. First, a node left with a single element is removed with that
element, which carries exactly 0 A (a blocked neutral, a stub line), and this repeats until no such node is left. Second,
a part of the network with no path to earth has its first node held at 0 V: its potential is undefined, but its
circulating currents are not.

## Per-transformer results

Per phase, I_H is the current entering the H terminal and I_X the current entering the X terminal, both towards the
neutral. For an autotransformer the series winding carries I_H and the common winding I_H + I_X. The neutral current is
3 (I_H + I_X). With a = V_H / V_X from the two base voltages, the effective GIC is

I_eff = | I_H + I_X / a |,

which is eq. 1 of the NERC *Transformer Thermal Impact Assessment White Paper* (October 2017),
I_dc,eq = I_H + (I_N/3 - I_H) V_X / V_H, taken as a magnitude, since I_N/3 - I_H is I_X. For a grounded-wye/delta unit it
is |I_H|.

The reactive loss is Q = k V I_eff (Mvar), with k from the file (Mvar per ampere at 1 pu) and V the case's voltage
magnitude at the HV bus. The guide gives no table of k by core type, so GridSim has no default: without k no loss is
reported.

The direction sweep solves at 1 V/km for E_N = cos theta, E_E = sin theta, theta = 0, step, 2 step, ... below 360 degrees, and reports
each transformer's largest I_eff and the first direction that reaches it.

## Verification

- A two-substation loop against its closed form: 100 V around 2.4 ohm gives 41.666... A, to 1e-12 relative.
- A blocked neutral gives exactly 0 A everywhere.
- Superposition: the result for (3, -7) V/km equals 3 times the north result minus 7 times the east result to 1e-12 of
  the largest current.
- The guide's worked example (Appendix II, six buses, three substations), against an independent re-derivation: the same
  network, written from the guide's Figure B-2, solved in exact rational arithmetic (Python) and cross-checked by a mesh
  analysis. Every current of Tables B-6 and B-7 (seven field directions, six currents, with Sub 2 grounded and with its
  neutral blocked by a 1 Mohm ground) matches it to 2e-12 and 1e-10 of the largest current, bounds derived from the
  network's condition number.
- The guide's printed tables, as a reference check. With the spherical distances every current of Table B-6 agrees to
  half a unit of its printed last digit. Table B-7 agrees to half a unit in all but eight cells, where the printed value
  is one printed unit from the correctly rounded value (largest gap 0.006 A from the exact figure). The cause of that
  residual in the guide's figures is not known: the 111.2 km per degree is inferred from the example, and no single
  constant fits Tables B-6 and B-7 together to half a unit. The table's line sources (Table B-5) agree to the printed
  0.1 V.
- The guide's tables state the distances and line sources as from-end minus to-end, and Table B-6 follows that sign;
  Table B-7 follows the physical sign of the path from the from end to the to end, which is GridSim's. The tests compare
  each table in its own sign.
- Two runs give the same bits; cosines come from the deterministic library.

## Scope

- A uniform field only: no non-uniform or time-varying field and no ground conductivity model. The field is an input; it
  is not computed from a magnetic field.
- Transformer heating is not modelled, only the reactive loss. No harmonic or relay effects.
- The reactive loss is reported, not fed back into a power flow.
- Two-winding units only; three-winding transformers are not supported. Such a unit is held in the case as three legs
  to a star-point bus, which has no usable base voltage to tell HV from LV, so a leg is refused by name (an undescribed
  transformer branch, or one whose buses do not have two different base voltages above 0). Describing the legs as
  separate two-winding units would not model the unit's DC paths.
- Verified on a closed form and the guide's worked example. The 20-bus test case of Horton et al. (IEEE Trans. Power
  Delivery 27(4), 2012) is not reproduced in the guide and has not been checked. Not compared with any vendor GIC module.
