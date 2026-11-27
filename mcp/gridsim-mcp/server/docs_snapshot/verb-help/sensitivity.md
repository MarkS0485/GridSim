### sensitivity

`gridsim sensitivity <case> --kind otdf|psdf|tap|dvdq|effectiveness [--branch <idx>] [--bus <id>] [--top 12]`

First-order sensitivities of a case. `--kind` chooses the quantity:

- `otdf` (needs `--branch` and `--bus`): outage transfer distribution factors, the DC flow on every branch per MW
  injected at `--bus` (withdrawn at the reference of its island) after branch `--branch` is out:
  `PTDF[l, bus] + LODF[l, k] * PTDF[k, bus]`, from the same factored B' as `ptdf` and `lodf`. A bridge outage prints that it
  ISLANDS the system and the factors are undefined (NaN in the result file), never an invented number. The outaged
  branch's own factor is 0.
- `psdf` (needs `--branch`): phase-shifter distribution factors, the DC change of every branch flow in MW per radian of
  phase shift on `--branch`. The sign is the solver's own (`PhaseShiftDeg` enters as `tap = a e^(j phi)`, so a positive shift
  reduces the From-to-To flow): for two parallel branches the shifted one gives `-1/(X1 + X2)` per unit (-250 MW/rad for
  two 0.2 pu branches on a 100 MVA base).
- `tap` (needs `--branch`): the AC change of every bus voltage per unit and per per cent of the tap ratio of `--branch`,
  the exact derivative of the solved state with the regulated set points and any reactive-limit pins held as they ended
  the solve.
- `dvdq` (needs `--bus`): the AC voltage change at every bus per MVAr and per MW injected at `--bus`. A bus that holds its
  voltage at the solved state (slack, or a regulating unit not at a limit) has a zero reactive response.
- `effectiveness` (needs `--bus` as the target): the effect on the target bus voltage of every candidate control, from one
  adjoint solve: reactive injection at each PQ bus (pu per MVAr), the tap ratio of each transformer (pu per unit ratio)
  and the voltage set point of each regulating bus (pu per pu), largest first.

The AC kinds solve the base case with the contingency study solver and options (as `cpf` and `nminus1` do) and linearise
the polar Newton equations there (the analytic Jacobian of the pi-model admittances, with ZIP load slopes). They are
refused with `OUT OF SCOPE` (exit 3) for a case with on-load tap changers, DC links, FACTS devices, static var
compensators, induction machines or transformer flow controls, whose outer loops are not in the Jacobian, and exit 3 when
the base case does not converge. The DC kinds use only series reactance, as `ptdf`. Exit 2 for a missing or unknown
`--kind`, a flag the kind does not use, a bus or branch the case does not have, or a branch that is out of service.

| Flag | Default | Meaning |
|---|---|---|
| `--kind` | required | `otdf`, `psdf`, `tap`, `dvdq` or `effectiveness`. |
| `--branch` | - | 0-based branch index: the outage (`otdf`), the shifter (`psdf`) or the transformer (`tap`). |
| `--bus` | - | Bus id: the injection (`otdf`, `dvdq`) or the target (`effectiveness`). |
| `--top` | 12 | Rows to list on the console (the result file has every row). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): a `factors`, `voltage` or `effectiveness` table with every row.
