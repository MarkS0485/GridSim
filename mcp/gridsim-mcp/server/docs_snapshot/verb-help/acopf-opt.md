### acopf-opt

`gridsim acopf-opt <case> [--objective cost|losses|rpo] [--controls <file.json>] [--rounding flip|neighbours] [--max-iter 150] [--out <file.csv|file.json>]`

The AC optimal power flow of [`acopf-full`](#acopf-full) (the same MIPS interior point method, copied, not changed)
with a choice of objective and with continuous transformer taps and bus shunts as controls (`AcOpfExtended`).
`cost` minimises the units' polynomial cost curves, as `acopf-full`. `losses` minimises the sum of the units' output,
which is the losses plus the fixed demand (a linear cost of 1 per MW on every unit). `rpo` (reactive power optimisation)
is `losses` with every unit that is not at a slack bus held at its case output, so the controls are the units' reactive
output (through the voltages), the taps and the shunts. `losses` and `rpo` accept units with no cost curve; `cost`
still refuses them. With no controls file, `cost` gives the same bits as `acopf-full`.

The controls file (`gridsim-opf-controls/1`, every member checked, all optional):

```
{ "format": "gridsim-opf-controls/1",
  "taps": ["T1", "T2"],
  "shunts": [ { "bus": 2, "bMinMvar": 0, "bMaxMvar": 100, "stepMvar": 10, "initialMvar": 0 } ] }
```

`taps` lists transformers by their unique branch name, or is the word `"oltcs"` for every OLTC of the case. A tap's
range and step come from its OLTC record, else 0.9 .. 1.1 in steps of 0.0125; the variable is 1/tau on the from side
(the MATPOWER branch model). A shunt adds a susceptance (MVAr at 1 pu, capacitive positive) to the bus's own case shunt.

`pst` adds phase-shifter angle controls, one per branch, after the shunts:

```
"pst": [ { "branch": "PS1", "minDeg": -30, "maxDeg": 30, "stepDeg": 1 } ]
```

The angle is the MATPOWER shift (positive as SHIFT) in degrees. `minDeg` and `maxDeg` default to the range of the
branch's active power flow control (PSS/E COD 3) and are needed when it has none; `stepDeg` defaults to that control's
step, else 0, which means the angle is continuous and is not rounded. The branch keeps its case tap ratio, and a branch
cannot have a tap control as well. An optional `"kind": "pst"` member is accepted.

After the continuous optimum each control is rounded to its grid (tau = MinTap + n step, B = bMinMvar + n step), to the
nearest point; a tie goes to the point nearer the case value (`initialMvar` for a shunt), then to the lower. The controls
are then fixed and the same objective is solved again. If that does not converge or breaks a limit, each control in
turn (taps first, in file order) is flipped to its other neighbour, and the first flip that converges within every limit
is kept. If none does, the run prints `rounded point infeasible` and the continuous optimum. This is a stated heuristic,
not a mixed-integer optimum.

`--rounding neighbours` solves every floor/ceil combination of the discrete controls instead (2^n solves for n controls
with two neighbours, in Gray-code order) and keeps the best feasible one; a tie goes to the lowest combination index.
More than 12 discrete controls are refused. The result is optimal over the floor/ceil neighbours of the continuous
optimum. It is not a global mixed-integer optimum. It ends `BestNeighbour`, or `NoNeighbourFeasible` (exit 3) when no
combination is feasible, and the result table gains `rounding_mode`, `rounding_combinations` and
`rounding_combination` in its summary.

Prints the continuous optimum (objective, generation, load, losses, every unit's P and Q, every control's value, the
binding limits), the rounding of each control, how the rounding ended and the discrete re-solve. Exit 0 done. Exit 1 for
a case or controls file it refuses (the reasons of `acopf-full`, an unknown member or name, a branch that is not a
transformer, crossed ranges). Exit 2 for an unknown `--rounding` mode. Exit 3 when the continuous optimum does not
converge (the iteration log is printed on stderr, no dispatch is shown) or the rounded point is infeasible.

When the continuous optimum fails, stderr names the status: `acopf-opt: not converged: ...` (the iteration cap),
`acopf-opt: numerical failure: ...` (the iteration broke down), or `acopf-opt: no solution: likely infeasible: ...`.
The last is status `Infeasible`: the run broke down after the feasibility measure stayed above its tolerance and above
half its value of four iterations before while the complementarity grew. That is the signature an interior point
method shows on a problem with no feasible point. It is evidence of infeasibility, not a proof. The message gives the
iterations and values of that window, the reason the iteration stopped, and the inequality constraints with the largest
final multipliers (at least a thousandth of the largest, eight at most), which are usually the ones in conflict. An
independent phase-1 solve (for example raising the ratings until a solution exists) confirms it.

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)): the `summary` rows as summary values and every row of the result table as the table `rows` (record, element, field, value). `--out` is unchanged: it still names its own result table, not a shared-writer file.

| Flag | Default | Meaning |
|---|---|---|
| `--objective` | cost | `cost`, `losses` or `rpo`. |
| `--controls` | none | A `gridsim-opf-controls/1` file of taps, shunts and phase-shifter angles to optimise. |
| `--rounding` | flip | `flip` (nearest point, then one flip at a time) or `neighbours` (the best floor/ceil combination, at most 12 controls). |
| `--max-iter` | 150 | Iteration cap of each interior point solve; at least 1. |
| `--out` | none | Write the result table (`record,element,field,value`) as CSV, or as JSON for a `.json` file. |
