### dist3ph-mesh

`gridsim dist3ph-mesh <case.json> [--steps <steps.json>] [--warm-start] [--tol <A>] [--max-iter <N>] [--scale <F>] [--pin-floating] [--hold-taps]`

`gridsim dist3ph-mesh --from-dist3ph <feeder.json> [--switch-z-pu <pu>] [--steps <steps.json>] [--tol <A>] [--max-iter <N>] [--scale <F>]`

Meshed, unbalanced load flow in the explicit phase frame (Core `GridSim.Core.Distribution.Meshed`). It is a new verb beside
`dist3ph`, which is unchanged: `dist3ph` is the radial forward-backward sweep on Kron-reduced 3x3 matrices, `dist3ph-mesh` solves
any topology (rings included) with every conductor a named wire (a, b, c, n, l1, l2, e, ...), the full primitive impedance matrix
of each branch (a four-wire line is 4x4, neutral included, nothing pre-reduced), explicit earthing and loads by connection. A
radial case agrees with `dist3ph` within a stated tolerance (a different algorithm, so not bit identity); `dist3ph` is not replaced.
Units are SI: volts to earth, ohms, siemens, volt-amperes, amperes. `<case.json>` is a strict `gridsim-dist3ph-mesh/1` document
(an unknown or missing key, a duplicate key or a wrong type exits 1 naming it). The licence's bus limit applies to it as to
every network, one bus per node of `nodes` (two for a `swer` case): a case over the limit is refused with exit 2 before it
is solved, as `--from-dist3ph` refuses a feeder. An example:

```json
{
  "schema": "gridsim-dist3ph-mesh/1",
  "name": "two-bus four-wire feeder",
  "nodes": [ {"name": "S", "wires": ["a","b","c","n"]}, {"name": "L", "wires": ["a","b","c","n"]} ],
  "sources": [ {"node": "S", "wires": ["a","b","c"], "magnitude_v": [7200,7200,7200], "angle_deg": [0,-120,120]} ],
  "branches": [ {"name": "line", "from": "S", "to": "L", "wires": ["a","b","c","n"],
                 "r_ohm": [[0.50,0.10,0.09,0.10],[0.10,0.52,0.11,0.10],[0.09,0.11,0.49,0.09],[0.10,0.10,0.09,0.80]],
                 "x_ohm": [[1.20,0.50,0.42,0.46],[0.50,1.15,0.48,0.44],[0.42,0.48,1.18,0.40],[0.46,0.44,0.40,1.30]]} ],
  "loads": [ {"name": "load", "node": "L", "connection": "wye", "nominal_v": 7200,
              "p_w": [60000,40000,90000], "q_var": [25000,12000,30000], "zip": [1,0,0]} ],
  "earthing": [ {"node": "S", "wire": "n", "r_ohm": 0} ]
}
```

Members: `nodes` (name and wires; a wire is a separate unknown at every node that lists it), `sources` (an ideal source fixes the
wires it names; `series_r_ohm` and `series_x_ohm`, one per wire, put it behind an impedance, for example a source earth electrode),
`branches` (`wires` in matrix order; `r_ohm` and `x_ohm` are the total matrices; optional `shunt_g_siemens` and `shunt_b_siemens`
to earth, half at each end; optional `kron_wire` declares that wire a multi-grounded neutral, eliminated by a recorded Kron
reduction, the only reduction ever applied), `loads` (`connection` is `wye` with a `return_wire`, default n, `wye-earthed`,
`wye-floating` with an internal star point, `delta` with legs a-b, b-c, c-a, or `line-line` with `from` and `to`; `p_w`, `q_var` per
leg at `nominal_v`, the leg's nominal voltage; `zip` is the constant-power, constant-current and constant-impedance fractions,
summing to 1, default constant power), `earthing` (a grounding impedance from a wire to earth, or to a named `grid`; zero is a
solid connection), `grids` (an earth grid with an impedance to remote earth, whose voltage is the ground potential rise) and
`transformers` (single-core units of two or more windings, each between `wire` and `return_wire`, or earth when `return_wire` is
omitted, with signed `turns` proportional to rated voltage and a non-zero leakage impedance; a three-phase bank is three units; a
centre-tapped 120/240 V service is one unit of three windings).

Three more optional members carry the elements of a distribution feeder that the primitive units alone would make you
build by hand (Core `RegulatedFrame`, `StepRegulators`, `TransformerBanks`). They are additive: a case without them
is read and solved exactly as before.

- `regulators`: step-voltage regulators between two nodes. Members: `name`, `from`, `to`, `connection` (`wye`, `open-delta` or
  `closed-delta`), `phases` (wye only: the regulated wires, for example `["a","b","c"]`), `taps` (integers, one per listed
  phase for wye, `[ab, cb]` for open delta, three equal values for closed delta; the ratio of a wye phase is
  `1 + step_pu * tap`), and optional `step_pu` (default 0.00625), `max_tap` (default 16), `winding_ohm` (default 1e-5, the
  ideal unit's tiny winding impedance) and `return_wire`. A wye regulator may add `ldc`, one line-drop compensator
  setting per listed phase (`vset_volts`, `bandwidth_volts`, `pt_ratio`, `ct_primary_amps`, optional `r_volts` and `x_volts`, on the
  120 V relay base), and `ganged` (the first phase's compensator controls the bank). Without `ldc` the taps are fixed.
  With `ldc` the single solve runs the compensator tap search: the load flow is repeated, one tap step per compensated
  phase and pass, until every phase is in its band or at a tap limit (Core `RegulatorTapSearch`); the taps found, the relay
  voltage and the band are printed and written. The search is refused with `--steps` (exit 2): give fixed taps there, or add
  `--hold-taps`.
- `banks`: three-phase transformer banks built from single-phase units. Members: `name`, `from`, `to`, `connection`
  (`grounded-wye-grounded-wye`, `delta-grounded-wye`, `wye-delta`, `delta-delta`, `open-wye-open-delta` or
  `open-delta-open-delta`), `kv_ll_from`, `kv_ll_to` (rated line-to-line kV), `unit_kva` (one single-phase unit),
  the impedance either as `r_percent` and `x_percent` (the total, split equally between the windings) or as
  `z_from_pu` and `z_to_pu` (`{"r": .., "x": ..}` on each winding's own base), optional `magnetising_pu` (`{"g": .., "b": ..}` on the
  from winding's base) and `from_return` / `to_return` (the return wire of a grounded-wye side; earth when omitted). An
  ungrounded-wye bank adds an internal star node, and a delta secondary is galvanically separate, so give it an earthed
  load or use `--pin-floating`. The bank's units and nodes are part of the result (their names start with the bank's name).
- `capacitors`: shunt capacitor banks, wye to earth. Members: `name`, `node`, `phases` (the wires with a unit), `q_var`
  (the reactive power supplied per phase at `nominal_v`, not negative) and `nominal_v` (line to earth). A bank is a
  constant-impedance leg (the model the incumbent `dist3ph` adapters use), so it scales with the voltage squared. It is not
  a load: the load factors (`--scale`, a step's `scale`) are divided back out of it, so only a step's `capacitors` member
  changes it. A zero load factor with a capacitor in the case is refused (exit 1), because it cannot be divided out.

Three more optional members are Core's North American split-phase and rural single-wire builders (`SplitPhase`, `Swer`). Each is
additive: a case without them is read as before.

- `split_phase_sources`: a 120/240 V source. Members: `node` (a node whose wires include `l1`, `l2` and `n`), `volts` (the full winding
  voltage, for example 240, above 0) and optional `series_r_ohm` and `series_x_ohm` (an impedance in each line). It fixes `l1` at
  +volts/2, `l2` at -volts/2 and the neutral `n` at 0 V, so with it the `sources` member may be left out.
- `centre_tap_transformers`: a centre-tap service transformer as one three-winding unit. Members: `name`, `primary_node`,
  `primary_wire`, optional `primary_return` (earth when omitted), `primary_volts` (rated, above 0), `secondary_node` (wires `l1`, `l2`, `n`),
  `secondary_volts` (the full secondary, for example 240; each half is half of it), `primary_r_ohm` and optional `primary_x_ohm`, `half_r_ohm`
  and optional `half_x_ohm` (leakage on each winding's own side). Magnetising current is neglected. The centre tap is earthed by an
  `earthing` entry on the secondary's `n`, as in the other transformer members.
- `swer`: a complete single-wire earth-return feeder, and the only member of its case (with `schema` and `name`; any other member is
  refused). Members: `emf_v` and optional `emf_angle_deg`, `source_earth_r_ohm` and `source_earth_x_ohm` (the source electrode),
  `line`, `load_earth_r_ohm` and `load_earth_x_ohm` (the load-end electrode) and `load` (`p_w`, `q_var`, `nominal_v` and an optional
  `zip`, default constant impedance). `line` is either the total impedance, `r_ohm`, `x_ohm` and an optional `shunt_b_siemens`, or a
  conductor and a length, `length_km`, `frequency_hz`, `earth_resistivity_ohm_m` and `conductor` (`resistance_ohm_per_km`, `gmr_m`,
  `radius_m`, `height_m`), from which Core's earth-return line constants give the impedance (the earth-return resistance included) and,
  with `"charging": true`, the shunt capacitance. The nodes are `S` (wire `a`) and `L` (wires `a` and `e`), the branch is `line` and the
  load `load`; the earth return of the line is the `branch_earth_return` table.

A step may switch banks: in the steps file a step's `"capacitors": {"C1": 0}` takes bank `C1` out of service for that
step (`1` puts it in, and is the default); any other value is refused. Taps are not switched by a step: a regulator
keeps the taps written in the case (or found by the search) for every step.

The calculation runs in a fixed order: validate wires and earthing; build the primitive admittance in element order; eliminate
nothing implicitly; start from the no-load solution (flat per phase when there are no transformers or shunts; flat by wire name
if that is singular); Newton in the phase frame with a fixed policy (full steps, no damping); the residual in amperes; report.
A case that cannot be solved as given is reported INCOMPLETE with the reasons named, never a number: a wye load whose neutral wire
is absent, a delta load with a wire missing (no return path), a connected set of wires with no reference to earth (a neutral that
is earthed nowhere and tied to nothing), a branch whose impedance matrix is singular. `--pin-floating` instead pins the first wire
of each unreferenced component at 0 V and records that in the notes (the usual case is an isolated, ungrounded secondary, where only
differences are physical). With `--steps` the verb runs a quasi-static series: `<steps.json>` is a strict
`gridsim-dist3ph-mesh-steps/1` document, `{"schema": "gridsim-dist3ph-mesh-steps/1", "steps": [{"label": "t0", "scale": 0.4,
"load_scale": {"load": 0.5}}]}`. By default every step is independent, so any step is reproducible alone and bit-identical to a
single run with the same factors; `--warm-start` starts each step from the previous converged step's solution instead (the same
answer within the tolerance, not claimed bit-identical). Per node and wire the result gives the complex voltage; per branch the
current of every conductor at both ends, neutral included, and the earth-return residual (the sum of the conductor currents,
which must return through earth; for a single-wire earth-return line it is the line current); per load leg the voltage, current and
power; source, earthing and grid currents. Exit 0 every step converged; 1 the file is missing or not a valid case; 2 usage; 3 a
step did not converge or is INCOMPLETE, or a compensator tap search did not settle (or, with `--hold-taps`, a compensated phase is
outside its band).

| Flag | Default | Meaning |
|---|---|---|
| `--steps` | - | A `gridsim-dist3ph-mesh-steps/1` file: solve one step per entry with its load factors. |
| `--warm-start` | off | Start each step from the previous converged step (needs `--steps`). |
| `--tol` | 1e-6 | Convergence limit on the largest current mismatch at any free unknown (amperes, above 0). |
| `--max-iter` | 40 | The iteration limit (at least 1). |
| `--scale` | 1 | A factor on every load's power (not below 0), applied before any step factors. |
| `--pin-floating` | off | Pin a reference on each unreferenced wire component instead of reporting INCOMPLETE. |
| `--from-dist3ph` | - | A `dist3ph` feeder file to lift instead of a mesh case (see below). |
| `--switch-z-pu` | - | With `--from-dist3ph`: the per-unit series impedance given to a closed switch (above 0). A closed switch is refused without it. |
| `--hold-taps` | off | With a regulator that has `ldc`: keep the written taps and report where each compensated phase stands against its band, without searching (single solve; also lets `--steps` run a case with `ldc`). |

`--from-dist3ph <feeder.json>` takes the place of `<case.json>`: it reads an existing `dist3ph` feeder file (v1 or `gridsim-dist3ph/2`,
the file the [dist3ph](#dist3ph) verb reads) and lifts it into the phase frame with Core's `MultiPhaseFeederAdapter`, so a feeder does not
have to be written again as a mesh case. The per-unit feeder becomes SI on each bus's own base (nodes named `bus<id>`, wires a, b, c), and its
transformers (delta / grounded wye, ungrounded wye / delta, delta / delta, open wye / open delta, open delta / open delta) and
step-voltage regulators come with it, at the taps the feeder file states (no compensator, so the taps are fixed). A closed switch is refused by
name unless `--switch-z-pu` gives it a series impedance (the Core tests use 1e-6). A feeder that holds something the frame does not carry (a
regulator ratio off the 0.00625 tap grid, a tap beyond 16, more than one transformer flag on a branch) is not solved: each such element is
listed on standard error and the exit code is 1. A delta secondary leaves a floating component, so the first wire is pinned as the
reference automatically (a note says so; compare the delta nodes line to line). Giving both `<case.json>` and `--from-dist3ph`, or
`--switch-z-pu` without `--from-dist3ph`, exits 2. The result is the usual one with the summary member `lifted_from_dist3ph` and an
`adapter_notes` table; `--steps`, `--scale` and the solver options work as for a mesh case. `dist3ph` is unchanged, and a lifted radial
feeder agrees with it only within the solver tolerances (a different algorithm).

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (case, `case_sha256`, steps, whether all converged and, for a single solve, the status,
iterations and mismatch) and the tables `steps`, `voltages`, `branch_currents`, `branch_earth_return`, `loads`, `sources`,
`earthing` and `grids`, one row set per step (the `step` column reads `-` for a single solve). A case with regulators adds a
`regulators` table (`regulator,connection,phase,tap,ratio,relay_v,band_low_v,band_high_v,in_band,at_limit`, the last four
filled for a compensated phase) and, when a search ran, the summary members `tap_search` (`SETTLED`, `HUNTING`, `NOTSETTLED` or
`LOADFLOWFAILED`), `tap_search_load_flows` and `taps_held`. A tap search that does not settle (the controllers would hunt, the
pass limit is reached, or a load flow inside it fails) exits 3.
