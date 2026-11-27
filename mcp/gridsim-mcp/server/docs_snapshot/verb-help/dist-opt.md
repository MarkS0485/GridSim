### dist-opt

`gridsim dist-opt <case> --mode capplace|vvo|phase-balance [--params <file.json>] [--out <file.csv|file.json>]`

Distribution optimisation in three modes. Each is deterministic: every search runs in a fixed order, uses no random
numbers, and gives ties to the lowest index. The settings come from a `gridsim-distopt/1` JSON file (`--params`), which
is read strictly: an unknown member, or a section for another mode, is refused by name.

- `capplace`: shunt capacitor placement and sizing on a balanced case. The objective (after Grainger and Lee, IEEE
  Trans. PAS-100(3), 1981) is the annual loss cost (losses x `energyCostPerMwh` x `hoursPerYear`) plus the capital cost
  (`fixedCostPerBank` per installed bank plus `costPerMvar` per Mvar). Each candidate bus has a list of bank sizes in
  Mvar, starting at 0 (`candidates`, or `defaultStepsMvar` for every PQ bus). A bank is a constant admittance added to
  the bus shunt. The search is a heuristic: greedy by marginal benefit (each round tries every candidate at its next
  size with a full Newton power flow and keeps the best), then a confirmation over every combination of one size down,
  no change or one size up on the buses holding a bank (3^m power flows). Above 8 banks the confirmation covers the 8
  the greedy search changed last, and the result lists the others as `unconfirmed`. A case with an OLTC or a
  transformer flow control is refused (the trial power flows do not regulate them).
- `vvo`: volt/VAr optimisation over the case's OLTC taps (`taps`, by transformer name; default every OLTC) and
  switched capacitor banks (`capacitors`: name, bus, Mvar, starting state). The objective is `losses` or `demand`
  (conservation voltage reduction: the real power the ZIP loads draw at their solved voltages), with every bus voltage
  in its band. A tap moves on the grid MinTap + k x StepPu; an OLTC with an impedance correction table takes its R and
  X from the table at each trial ratio. Every OLTC of the case must be among `taps`, and a transformer flow control is
  refused, since the trial power flows do not regulate them. Discrete coordinate descent in a fixed order (taps, then
  capacitors), then the same neighbour confirmation over every control (refused above 8 controls unless `confirm` is
  false). The result is a local discrete optimum.
- `phase-balance`: moves single-phase loads between phases on a `dist3ph-mesh` case file. The proxy objective is the
  largest deviation of a phase's real power from the mean. When there are at most 3^12 = 531 441 assignments (the
  product of each movable load's allowed phase count), all are searched (the lexicographically smallest best assignment
  wins); above that, greedy, largest load first, a load to the lightest phase and a generating load to the heaviest. One `dist3ph-mesh` solve before
  and one after report the head-branch neutral current and the voltage unbalance factor at the head node.

In `capplace` and `vvo`, trials are compared on the total voltage violation first, then the objective, so an
infeasible start first moves into the band. The bus limits are the case's own where it has them, else `vminPu` and
`vmaxPu` (default 0.95 and 1.05). The `common` section sets `vminPu`, `vmaxPu`, `tolerancePu` (Newton, default 1e-10),
`maxIterations` (30), `confirm` (true) and `maxRounds` (100). It is not read by `phase-balance`, whose section is
`phaseBalance`: `movable` (load names, each with optional `phases`), `phases` (default a, b, c), `headBranch` and
`neutralWire` (default n).

Prints each step, the result and how the search ended. Exit 0 done. Exit 1 for a file or setting it refuses. Exit 3 when
the result is outside the voltage band, the search stopped at its round cap, or a `phase-balance` solve did not converge.

| Flag | Default | Meaning |
|---|---|---|
| `--mode` | none (required) | `capplace`, `vvo` or `phase-balance`. |
| `--params` | none | The `gridsim-distopt/1` settings file. |
| `--out` | none | Write the result table (`record,element,field,value`) as CSV, or as JSON for a `.json` file. |
