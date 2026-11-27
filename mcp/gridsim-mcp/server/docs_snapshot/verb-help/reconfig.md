### reconfig

`gridsim reconfig <case> [--switchable <i,j,...>] [--respect-ratings] [--min-gain-mw 1e-6] [--max-swaps 100] [--out <file.csv|file.json>]`

Distribution feeder reconfiguration for loss reduction by branch exchange, best improvement (`FeederReconfiguration`;
Civanlar et al. 1988, Baran and Wu 1989). The start is the case's in-service branches, which must be radial and connected
(a meshed or disconnected start is refused, naming the loop or the bus). The switchable branches are every branch that
is not a transformer, or `--switchable` (a transformer there is refused). Each pass tries, for every open switchable
branch in index order, closing it and opening each in-service switchable branch on the loop it makes, in index order;
each trial is solved by the backward/forward sweep of `pf` and rejected if it does not converge, a bus falls below its
lower voltage limit (else 0.94 pu) or, with `--respect-ratings`, a branch is above RATE_A. The trial of lowest loss is
taken (a later one wins only when lower by more than 1e-9 MW, so ties go to the lower closed, then opened, index), and it
stops when the best saving is below `--min-gain-mw` or at `--max-swaps`. Branch exchange is a local search: the result
is not guaranteed to be the global optimum. Balanced feeders only (not `dist3ph`). A MATPOWER `.m` case is read with
its out-of-service (status 0) branches kept as open switches (every other verb drops them), so the tie switches of the
published feeders, such as case33bw's branches 33 to 37, are there to close.

Prints the base losses, each swap (closed, opened, losses after), the final open branches, losses and minimum voltage.
Exit 0 done. Exit 1 for a start or switchable list it refuses.

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)): the `summary` rows as summary values and every row of the result table as the table `rows`. `--out` is unchanged: it still names its own result table, not a shared-writer file.

| Flag | Default | Meaning |
|---|---|---|
| `--switchable` | every non-transformer branch | A comma list of branch indices (from 0) that may be switched. |
| `--respect-ratings` | off | Reject a configuration with a branch above RATE_A (no value). |
| `--min-gain-mw` | 1e-6 | Stop when the best swap saves less than this (MW). |
| `--max-swaps` | 100 | The most swaps to make; reaching it is reported. |
| `--out` | none | Write the result table (`record,element,field,value`) as CSV, or as JSON for a `.json` file. |
