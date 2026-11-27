### qv-curve

`gridsim qv-curve <case> --bus <id> [--vmin 0.4] [--vmax 1.2] [--step 0.01]`

The QV curve of one bus: the reactive power a synthetic voltage-controlling unit at the bus must supply to hold each
voltage. A copy of the case holds the unit with an unbounded reactive range and makes the bus a PV bus; units already on
the bus are held at their solved base-case reactive output, so the curve is the EXTRA compensation required and is zero at
the operating point. The voltage is swept down and up from the operating point on a grid of `--step` (anchored there),
each point solved with the contingency study solver. A point that does not converge ends that branch of the sweep and is
recorded in the table (`converged` false), not skipped. The report gives the grid minimum, a nose estimate (the parabola
through the three samples round the minimum, exact when Q(V) is quadratic, as it is for a lossless line with no real
power) and the reactive margin at the operating point: `-Qnose`, the reactive power the bus can still lose before the
curve no longer meets the zero line (negative: the operating point is already past the nose). For a slack bus the verb
refuses (`OUT OF SCOPE`, exit 2); exit 3 when the base case does not converge or the minimum is not bracketed by the
converged points.

| Flag | Default | Meaning |
|---|---|---|
| `--bus` | required | Bus id (not the slack). |
| `--vmin` | 0.4 | Lowest voltage swept (pu); above 0. |
| `--vmax` | 1.2 | Highest voltage swept (pu); above `--vmin`. |
| `--step` | 0.01 | Voltage step (pu); above 0. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (operating voltage, margin, nose) and a `curve` table.
