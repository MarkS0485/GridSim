### losses

`gridsim losses [<case>]`

Loss allocation on the solved base case, under a header naming the case (`Loss allocation - <case> (N buses, M
branches)`): total P and Q losses, the real power drawn by bus shunt conductances (`Bus shunt conductance`, only when
the case has any: it is in the solver's injection total but in no branch), the reconciliation residual (rounding once
the shunt draw is accounted for), losses by zone (when the case has zones) and the 20 lossiest branches. `<case>`
defaults to `case9` and is resolved like every other case argument ([Case arguments](#case-arguments)): an unknown
name exits 2 naming it, a missing or unloadable `.m` file or case folder exits 1, and a second case argument exits 2.
Solves on the shared dense / sparse rule. Exit 3 if the base case does not converge. No flags.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).
