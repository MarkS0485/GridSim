### qv-modal

`gridsim qv-modal <case> [--modes 5] [--out <file.json>]`

QV modal analysis (Gao, Morison and Kundur, IEEE Trans. PWRS 7(4), 1992; Kundur section 14.3) at the case's Newton power
flow (the shared engine rule, 100 iterations, reactive limits on, as `pf`). The power-flow Jacobian is reduced to the
reactive-power and voltage relation at the PQ buses, J_R = J_QV - J_Qtheta J_Ptheta^-1 J_PV (a PV bus held at a reactive
limit counts as PQ), and its eigenvalues are found with the general real eigen solver that `modal-full` uses. Each mode
prints its eigenvalue and the ten buses with the largest participation factor (P_k = left_k x right_k, scaled so a mode's
factors sum to 1). An eigenvalue above 0 is voltage stable, below 0 unstable, and near 0 close to the voltage stability
limit; the bus with the largest participation is where that mode is weakest. See
[technical ch. 29](<../technical/0029 OPF, CPF, taps, distribution factors and loss allocation.md>).

| Flag | Default | Meaning |
|---|---|---|
| `--modes` | 5 | How many modes to report, smallest absolute eigenvalue first; a whole number of 1 or more. |
| `--out` | none | Write the result as JSON (`gridsim-qv-modal/1`: the PQ buses and, per mode, the eigenvalue and every bus's participation); an existing file is never overwritten. |

The JSON from `--out` is the result file: the verb is not on the shared `--json`/`--csv` writer. Scope: the standard
power-flow Jacobian only (reactive limits as solved, no dynamic models, no branch or generator participation). A case
with HVDC links, FACTS devices, static var compensators or induction machines is refused: the reduced Jacobian is built
from the network alone, so it would describe a different system.

J_R is not symmetric, so a stable state can have complex pairs of eigenvalues; each mode is judged on its real part, and
a complex mode prints as `a + jb`. Exit 0 when every reported eigenvalue has a positive real part. Exit 3 when the power
flow does not converge or a reported eigenvalue has a real part of 0 or below. Exit 1 for a case that cannot be loaded
or analysed (a singular J_Ptheta, a defective J_R, or a case with the coupled devices above). Exit 2 for usage.
