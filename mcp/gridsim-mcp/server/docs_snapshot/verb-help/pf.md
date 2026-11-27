### pf

`gridsim pf <case> [--distributed-slack pmax|pg|equal] [--switched-shunts <file.json>] [--area-interchange <file.json>] [--remote-vreg-file <file.json>] [--top 12] [--engine newton|fdxb|fdbx|gs] [--gs-accel 1.0] [--max-q-passes 100] [--q-revert] [--max-iter 100] [--diverge-limit 10|off]`

One AC power flow on the engine the shared rule picks (radial sweep, dense or sparse Newton), 100 iterations with
reactive limits on, printed: convergence, iterations, mismatch, losses, the voltage range and the `--top` lowest bus
voltages. Four opt-in controls wrap the unchanged engine as outer loops (like the tap-changer loop), so the dense and
sparse engines give the same bits with them too. They nest in this order: engine, distributed slack, switched shunts,
remote voltage, area interchange.

- `--distributed-slack pmax|pg|equal`: the real-power balance (losses and any scheduling error) is shared among the
  in-service units by participation factors proportional to Pmax, to scheduled Pg, or equal, instead of all landing on
  the reference bus. Each pass allocates the imbalance K, solves, and adds the reference bus's excess over its own
  allocation to K, until that excess is at most 1e-4 MW; units are held within [Pmin, Pmax] (water filling). The output
  lists the passes, K and each unit's move (the `--top` largest). A model with several reference buses, one in each
  network island, shares one imbalance per island among that island's units; two reference buses in one connected
  network are refused, naming them (exit 1). Every unit at a limit with imbalance left, or no settling within 50
  passes, is NOT CONVERGED (exit 3).
- `--switched-shunts <file.json>`: discrete shunt banks switched in whole steps to keep their bus voltage in a band.
  After each converged solve a bank below its band switches one step more capacitive, above it one step less, within
  its step range; a bank that reverses direction is frozen (it hunts across a band narrower than one step). One step
  adds `stepMvar` (MVAr at 1 pu, positive capacitive) to the bus Bs. The file:
  `{ "format": "gridsim-switched-shunts/1", "shunts": [ { "bus": 5, "stepMvar": 10, "minSteps": 0, "maxSteps": 4,
  "initialSteps": 0, "vminPu": 0.97, "vmaxPu": 1.03, "name": "C5" } ] }` (bus, stepMvar, maxSteps, vminPu, vmaxPu
  required; every key checked). The output lists each bank's steps and bus voltage. Not settled (a bank frozen outside
  its band, or 50 passes) exits 3.
- `--remote-vreg-file <file.json>`: a PV bus holds the voltage of another bus. The controlling bus stays PV in the
  Newton solve and its set point is moved between solves: e = target - V at the regulated bus, then a secant step on
  that control's own history (the first step adds e), the set point kept in [0.8, 1.2] pu, until every regulated bus is
  within 1e-8 pu of its target (30 passes at most). A controlling bus the reactive-limit loop turned PQ, at the limit
  the target pushes against, is AT Q LIMIT and its set point is frozen. Refused (exit 1): two controlling buses for one
  regulated bus, a regulated bus that is PV or slack, or one in another island. The file:
  `{ "format": "gridsim-remote-vreg/1", "controls": [ { "generatorBus": 2, "regulatedBus": 3, "targetVpu": 1.0 } ] }`
  (every key required and checked). The output lists each control as `bus 2 -> bus 3: OK` (or AT Q LIMIT, AT SET-POINT
  LIMIT, NOT SETTLED) with the regulated voltage and the set point.
- `--area-interchange <file.json>`: each controlled area holds its net export at the desired value by moving the units
  at its swing bus. The export is the sum, over the in-service branches with one end in the area, of the active power at
  the branch's metered end: as it is when that end is in the area, negated when it is not, so a tie's losses fall on the
  area at the far end from the meter. After each solve, an area more than its tolerance off has the difference added to
  its swing units, shared by (Pmax - Pmin) within their limits (equally when a range is zero or unbounded), and the case
  is solved again (20 passes at most). An area whose units all reach a limit is AT LIMIT and is not moved again; that is
  still exit 0. The area holding the slack bus cannot be controlled (exit 1): the slack absorbs the remainder. List it
  under `reportAreas` to have its export printed. The file:
  `{ "format": "gridsim-area-interchange/1", "areas": [ { "area": 2, "swingBus": 2, "desiredExportMw": -30,
  "toleranceMw": 0.1, "buses": [2, 3] } ], "reportAreas": [ { "area": 1, "buses": [1] } ], "meteredEnds": [ { "from": 1,
  "to": 2, "meteredBus": 2 } ] }` (`reportAreas` and `meteredEnds` optional; a metered end may add `name` to pick one of
  parallel branches; an unlisted branch is metered at its from end). Area interchange and `--distributed-slack` both move
  unit real power, so together they exit 2. The output lists each area as `area 2: OK` (or AT LIMIT, NOT SETTLED,
  REPORTED) with its export, desired value, tolerance and the MW added.

A PSS/E `.raw` case gives the same data with the global switches `--area-control` and `--remote-vreg` (giving a file as
well exits 2).

`--engine` picks the solution method by name. `newton` (or no `--engine`) is the shared rule above, unchanged. `fdxb`
and `fdbx` are the fast-decoupled load flow, XB and BX versions, with B' and B'' built as MATPOWER's `makeB.m` builds
them; `gs` is Gauss-Seidel as MATPOWER's `gausspf.m` iterates it (PQ buses, then PV buses, in model order), with the
acceleration factor `--gs-accel` (1.0 is plain Gauss-Seidel). All three use Newton's convergence test (the largest P and
Q mismatch below the tolerance, in pu) and the same reactive-limit loop, so a converged answer is the same solution as
Newton's to the tolerance. The fast-decoupled engines take up to 100 iterations, Gauss-Seidel up to 10000. They are
offered for comparison and for PSS/E script migration; Newton stays the recommended engine. They do not run with the
outer-loop controls (`--distributed-slack`, switched shunts, remote voltage control, area interchange: exit 2) or on a
case with HVDC links, FACTS devices, static var compensators or induction machines (exit 1). The fast-decoupled engines
also refuse a branch with X = 0 (purely resistive), naming it (exit 1): both B' variants drop R from one matrix, so such a
branch has no admittance there; Newton and Gauss-Seidel solve it. The header line names the
engine, and the result file gains `engine` (and `gs_acceleration`); without `--engine` both are as before.

The reactive-limit loop switches a generator bus from PV to PQ when its reactive output passes a limit, and solves again,
for at most 100 passes by default. A very large case can need more: if the last permitted pass still switched a bus, `pf` reports NOT CONVERGED
although the mismatch of that last solve is tiny, because the limits had not settled. Two flags change that. With
`--max-q-passes <N>` (a whole number, 1 or more) the loop may run N passes. With `--q-revert` a bus that was switched to
PQ returns to PV (once per solve) when its solved voltage shows the limit is no longer binding, so the loop settles in fewer passes (about
half on the large synthetic cases). Both apply to every `--engine`. `--q-revert` is off unless given. Without either flag the loop runs up to 100 passes with no
revert. A case that settles in fewer passes gives the same bytes at any cap. The two can be combined.

A solve that is running away is stopped early. When the largest power mismatch of an iteration rises above both 10 pu and
the mismatch the pass started from, `pf` stops that pass and reports `DIVERGED` instead of `NOT CONVERGED`, with the
iteration it stopped at and the state at that point, which is not a solution. Above 10 pu the simulation is
fundamentally broken and no starting point will fix it, so continuing to the iteration cap only spends time. The limit is
10 pu and not lower on purpose: a run that peaks at a few pu (the synthetic 90,000-bus case peaked at 4.1 pu) is still a
plausible but wrong state, and that state is useful evidence of an error in the model. The starting mismatch is part of
the test because a flat start on a large case can itself be above 10 pu. `--diverge-limit <X>` sets the limit in pu
(above 0), so a user who knows their models can lower it to save time. `--diverge-limit off` runs every pass to the
iteration cap, as before. `--max-iter <N>` (a whole number, 1 or more) sets that cap; without it the cap is 100, and 10000
for `--engine gs`. The stop applies to every `--engine`, but not to the backward/forward sweep that solves a radial
feeder, which tests the voltage change and not the power mismatch. It reads only the mismatch the iteration has already computed, and
changes no arithmetic, so a solve that converges gives the same bytes with the stop on or off. The exit status of a
diverged run is 3, as for any run that did not converge; the result file is written only for a converged run.

| Flag | Default | Meaning |
|---|---|---|
| `--distributed-slack` | off | `pmax`, `pg` or `equal`; any other value exits 2. |
| `--switched-shunts` | - | A `gridsim-switched-shunts/1` file; a missing or invalid file exits 1. |
| `--area-interchange` | - | A `gridsim-area-interchange/1` file; a missing or invalid file exits 1. |
| `--remote-vreg-file` | - | A `gridsim-remote-vreg/1` file; a missing or invalid file exits 1. |
| `--top` | 12 | Lowest voltages and largest slack moves to list; 0 or more (a negative value exits 2). |
| `--engine` | newton | `newton`, `fdxb`, `fdbx` or `gs`; any other value exits 2. |
| `--gs-accel` | 1.0 | Gauss-Seidel acceleration factor, above 0 and below 2; only with `--engine gs` (otherwise exit 2). |
| `--max-q-passes` | 100 | The most reactive-limit (PV to PQ) passes; a whole number, 1 or more (anything else exits 2). |
| `--q-revert` | off | A switch with no value: a bus switched to PQ may return to PV. Off keeps the one-way switching. |
| `--max-iter` | 100 (Gauss-Seidel 10000) | The most iterations per pass; a whole number, 1 or more (anything else exits 2). |
| `--diverge-limit` | 10 | The largest mismatch (pu, above 0) a pass may reach, above its starting mismatch, before it stops with `DIVERGED`; `off` never stops it early. Anything else exits 2. |

Exit 0 converged (and shunts, remote controls and areas settled; AT LIMIT and AT Q LIMIT count as settled); 3 not
converged or not settled; 1 on a load error, a bad file or a refused control; 2 on a usage error (an unknown
`--distributed-slack` rule, a flag of another verb, area interchange with `--distributed-slack`, a file and the matching
`.raw` switch together).

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (case, convergence, losses, voltage range, the slack and shunt outcome), a
`buses` table (every bus: |V|, angle, net injection), and with the options a `slack_units` table (every participating
unit), a `switched_shunts` table, a `remote_vreg` table (each control) and an `areas` table (each area), with the pass
counts and settled flags in the summary. Without those options the file is exactly as before. A solve that does not converge writes no file; unsettled shunts write the file
with exit code 3 inside.
