### reduce

`gridsim reduce <case> --keep <ids|file> --method ward|xward|rei [--rei-groups gen|gen,load] [--out <case.m|case.json>]`

`gridsim reduce <case> --keep <ids|file> --method sc [--out <case.json>]`

`gridsim reduce <case> --method dynamic --groups <file> | --coherency <r> --xdp <pu> [--out <case.m|case.json>]`

A static network equivalent (`NetworkReduction`, `GridSim.Core.PowerFlow.Equivalents`): the buses named by `--keep` stay
as they are and every other bus (the external network) is replaced by elements on the boundary, the kept buses with a
branch to an external bus. `ward` is the Ward equivalent: equivalent branches, shunts and constant-power injections.
`xward` is the extended Ward: Ward plus one voltage-controlled source per boundary bus standing in for the external PV
buses. `rei` is the REI equivalent: the external generators (and, with `--rei-groups gen,load`, the external loads as a
second group) are moved to one fictitious bus per group through a zero-power-balance network, then Ward. The base case is
solved with reactive limits off and a 1e-10 pu tolerance; the reduced case is solved from a flat start with the same
options. See [technical ch. 40](<../technical/0040 Network equivalents.md>) for the equations and the error bounds.

Prints the boundary, each element (pu on the system base), the notes (what was removed and not carried) and the base-case
check, the largest |V_reduced - V_full| at the boundary. `--out` writes the reduced case as MATPOWER (`.m`) or a v2 JSON
case (`.json`) through the existing writers, never over an existing file; each writer keeps its own rules (a v2 JSON
case needs every bus base kV above 0, so a case such as case14 with base kV 0 is written as `.m`). Refused, naming the element: a slack bus outside
the kept set, an external network not connected to the boundary, a phase shifter in the external network (the equivalent
is not reciprocal), a ZIP load, HVDC, FACTS, SVC, induction machine, OLTC, flow control or boundary touching the external
network, and a kept set that is empty or holds every bus. Exit 0 done. Exit 1 for a case it cannot load or a reduction it
refuses. Exit 3 when the base case or the reduced case does not converge.

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)): the summary (case, method, bus counts, the base-case check), the tables `elements` (kind, name, buses, real and imaginary part in pu), `boundary` and `notes`. `--out` is unchanged: it still names the reduced case, not a shared-writer file.

| Flag | Default | Meaning |
|---|---|---|
| `--keep` | required | The bus ids to keep: a comma list (`1,2,3,4,5`) or a file of ids separated by commas, spaces or new lines. Not used by `dynamic`. |
| `--method` | required | `ward`, `xward` (extended Ward), `rei`, `sc` (short-circuit equivalent) or `dynamic` (coherency-based dynamic equivalent). |
| `--rei-groups` | `gen` | REI only: `gen` (one group, the external generators) or `gen,load` (a second group for the external loads). |
| `--out` | none | Write the reduced case to this new `.m` or `.json` file. With `dynamic`, also `<name>.dynamics.json` beside it. |
| `--groups` | none | `dynamic` only: a file of coherent groups, one per line, each a list of bus ids whose in-service units form the group (`#` starts a comment line). |
| `--coherency` | none | `dynamic` only: find this many groups by slow coherency instead of reading `--groups`. |
| `--xdp` | required with `dynamic` | The classical transient reactance X'd given to every machine, pu on its own base. |

**`--method sc`**, the short-circuit equivalent (`ShortCircuitEquivalent`). No power flow is run. The positive-, negative-
and zero-sequence networks of the IEC 60909 studies are Kron-reduced onto the boundary and written as equivalent branches
(with zero-sequence data, or delta/delta windings where the zero sequence is not coupled) and one fictitious source bus
per boundary bus (a branch and a synchronous machine carrying the positive-sequence row sum, with a grounded-wye/delta
winding carrying the zero-sequence one). The fault studies on the reduced case then give the full case's Ik'' at every
kept bus, for three-phase, line-to-line and earth faults. It prints the elements, the external network's Thevenin
impedances Z1, Z2 and Z0 at each boundary bus, the notes and a check, the IEC 60909 three-phase Ik'' at each boundary bus
with a base kV on the full and the reduced case (`NOT RUN` when no boundary bus has a base kV). Refused, naming the
element: a converter-interfaced unit or a machine with X2 different from X''d in the external network, a zero-sequence
mutual coupling across the boundary, and the external elements the Ward equivalent refuses except ZIP loads. Loads are
not represented and the reduced case's power flow is not the base case. Apply the IEC 60909 correction factors to the
full case before reducing it, never to the reduced case. A boundary bus that the external network gives no path in a
sequence has an infinite Thevenin impedance there; the other buses keep theirs. `--out` writes a JSON v2 case that
carries every sequence field (the equivalent branches' windings, machine zero-sequence impedances and kept mutual
couplings), reads it back and prints `reload check`, the largest relative difference of the three-phase, line-to-line,
single- and double-line-to-earth Ik'' at the kept buses between the equivalent and the written case (0 expected: the
numbers are written exactly). A `.m` output is refused (exit 2): MATPOWER holds no X''d and no sequence data, so the
written case's fault currents would differ. Result files add the table `thevenin` and the summary value
`max_reload_ikpp_rel`.

**`--method dynamic`**, the coherency-based dynamic equivalent (`CoherencyGrouping`, `InertialAggregation`). Every bus is
kept. Each group of coherent machines, from `--groups` or found by slow coherency with `--coherency`, becomes one
classical (GENCLS) machine: S_eq the summed rating, H_eq and D_eq rating-weighted, X'd_eq the parallel combination. A
group on several buses moves to a new bus at the rating-weighted voltage, its terminal buses absorbed through ideal
transformers, so the base case solves the reduced case. Every machine takes the X'd of `--xdp` and no damping (tagged
Synthetic in the dynamics file). It prints the groups, the notes and the base-case check, the largest
|V_reduced - V_full| over the kept buses (V_eq at an equivalent bus). Exciters, governors and stabilisers are not
aggregated. Result files: the summary and the tables `groups` and `notes`. Exit codes as above.
