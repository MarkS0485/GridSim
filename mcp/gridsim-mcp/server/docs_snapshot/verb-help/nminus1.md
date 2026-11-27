### nminus1

`gridsim nminus1 [<case>] [--dir <path>] [--global-vlimits] [--outage-types branch,gen,bus] [--outage-dispatch reference|droop|inertia] [--post-fault-rating A|B|C] [--batched-lanes N] [--ras <file>]` (alias `n-1`)

Sequential N-1 branch outage screening on one model: each branch out in turn, re-solved, classified secure /
violations / islanded. Default model `data/uk`; a positional case or `--dir` selects another
([Case arguments](#case-arguments)). An outage that does not solve prints `ISLANDED` when it splits the network (the
post-outage network has more connected parts than the case) and `NO SOLUTION` when the network is still connected but
the power flow found no solution (a voltage collapse, for example `case14` branch `1-2`), with its reason: `islanded`
or `no solution` for a non-converged solve, or `solver error: <type>: <message>` when the solver threw. The count line
says `N islanded`, and `M not solved` when there are any. Both fail the verdict (exit 3). The result file's `islanded`
counts only the splits and `not_solved` counts the rest. `nminus2` and `n-1-1` label rows the same way.

The three branch screens share these rules:

- **Options.** The contingency option set and the tap-changer loop ([Solver selection](#solver-selection)).
- **Base case first.** The intact network is solved with the same options and printed once: `Base case: secure (...)`
  or `Base case: N pre-existing violation(s), reported once here and not counted against any outage: <list>`. A base
  case that does not converge exits 3 with `error: <verb>: the base case of <model> did not converge (...)`, before any
  outage is screened.
- **Netting.** A violation counts against an outage only when its branch, boundary or bus was not already violating in
  the base case. Status `VIOLATIONxN` counts those new violations; `base only` marks a converged outage that adds none
  while the base case's own violations remain in its state (counted as secure, and noted under the footer); `secure`
  means no violation at all.
- **Verdict.** The last line: exit 0 with `Verdict: SECURE (exit 0).` when the base case and every screened outage are
  secure; exit 3 with `Verdict: NOT SECURE, ... (exit 3).` when the base case has a violation or any outage violates,
  islands or does not solve (and, for `nminus2` and `n-1-1`, when a single outage was left out of the pairing because
  it already fails).
- **Voltage limits.** Each bus is judged against its own limits where the case gives them (MATPOWER bus columns 12 and
  13, VMAX and VMIN; JSON `vminPu` and `vmaxPu` in `buses.json`), and against the one operational band (0.94-1.06 pu)
  on any side a bus leaves unset; a case with no per-bus limits is judged against the band throughout.
  `--global-vlimits` judges every bus against the band and ignores the per-bus data. `--bus-vlimits` is accepted and
  does nothing (per-bus limits are on by default); giving it with `--global-vlimits` exits 2. Both flags are accepted
  by `nminus1`, `nminus2`, `n-1-1` and `nminus1-sweep` only; on any other verb they exit 2. Because every MATPOWER file carries VMAX and
  VMIN, the choice changes verdicts on bundled MATPOWER cases. The ranking of outages for `nminus2` and `n-1-1`
  candidates uses the band.
- **Post-fault rating.** By default every state's branch loading is judged against rating A (`RateA`, MATPOWER column
  6, JSON `ratingMva`). `--post-fault-rating B` or `C` judges each outage state against the branch's rating B
  (short-term: MATPOWER RATE_B, JSON `ratingBMva`) or C (emergency: RATE_C, `ratingCMva`), and a branch that has no B or
  C rating (0) against its rating A; the base case stays on rating A. This follows NETS SQSS v2.10 (docs/reference
  S036, 8 April 2025): no loading above the pre-fault rating before a fault (for example paragraph 2.9.1) and no
  "unacceptable overloading" after a secured event (paragraph 5.1.8), which the Definitions put at loading beyond the
  equipment's "specified time-related capability". The meaning of MATPOWER's rating columns (A long term, B short
  term, C emergency) is MATPOWER's caseformat convention [VERIFY: not in docs/reference]. `A` is the default and
  changes nothing. Accepted by `nminus1`, `nminus2` and `n-1-1` only; elsewhere, or with any other value, it exits 2.
  With it the header says which rating is used and the result file adds `post_fault_rating`.

`--batched-lanes N` (N349, `nminus1` only): solve the branch outages with the batched contingency sweep, N lanes posting
their Newton steps to one batched linear solve (0 = one lane per processor, at most 1024). Every number is bit-identical
to the default sequential screen (the CPU batched backend is the same sparse LU on each system); only the speed differs,
and a `note:` line on stderr says the batched sweep ran. Generator and bus outages (`--outage-types`) are screened as
before. A value that is not a whole number from 0 to 1024 exits 2.

`nminus1` alone also takes `--outage-types`: a comma list of `branch`, `gen` and `bus` (default `branch`, the screen
above). `gen` takes each in-service generating unit out in turn (NETS SQSS v2.10 paragraph 5.1.2, a single generating
unit): its scheduled output is lost and the reference bus picks it up in the re-solve. `bus` takes each bus out with
every branch touching it, every unit on it and its load: the bus-branch model's stand-in for a section of busbar
(paragraph 5.1.5). The load and generation an outage took are printed under its row (`lost load ... MW`, `lost
generation ... MW (picked up by the reference)`), reported but not judged (the SQSS loss-of-supply limits of Table 5.1
are not modelled). When an outage takes the last unit of the reference bus, or the reference bus itself, the reference
moves to the bus of the largest remaining unit by |Pg| in the same connected part of the network, and the row says
`reference moved from bus X to bus Y`; with no unit left there the outage is `ISLANDED`. Rows are printed worst first
(branch outages first among equals, then generators, then buses). With a non-default choice the header reads `N-1
screening (<kinds> outages)`, and the result file adds an `outage_details` table
(`outage,kind,lost_load_mw,lost_generation_mw,note`). `--outage-types` on another verb, or with an unknown kind, exits 2.

`--outage-dispatch` (N124, `nminus1` with `gen` or `bus` outages only) chooses who picks up the real power such an
outage takes away before the re-solve. `reference` (the default, also `slack`) is the screen above: the reference bus
takes it all, as PSS/E AC contingency analysis does with its dispatch disabled and PowerFactory with "as dispatched".
`droop` shares the net lost power (lost generation less lost load) among the remaining synchronous units in proportion
to their MBase, the primary-response gain MBase / R with one droop R for every unit (the case carries no per-unit
droop; PSS/E dispatch by governor droop, PowerFactory "according to primary control"); `inertia` shares it in
proportion to H x MBase (PSS/E dispatch by inertia, PowerFactory "according to inertias"). Converter plant,
power-flow-only units and units with no path to a reference bus take no share. Each unit stays within its Pmin and
Pmax: a unit whose share exceeds its headroom is held at its limit and the rest goes to the others; what no unit can
take is left to the reference. The reference then carries only its own share, the change in losses and any remainder.
The detail line reads `lost generation ... MW (shared by governor droop)` (or `by inertia`) and the note says how many
units shared it, how many met a limit and any MW left to the reference, for example `lost 60.0 MW shared by governor
droop over 2 unit(s)`. With `--outage-dispatch` on another verb, without `gen` or `bus` in `--outage-types`, or with
another value, it exits 2.

`--ras <file>` (`nminus1` and `nminus2`) runs remedial action schemes inside each contingency. The file is a
`gridsim-ras/1` document; [ch. 11](<0011 Contingency analysis.md#remedial-action-schemes---ras>) describes it. For each
contingency the schemes are armed from the base case and the outage, then level by level every armed scheme whose
triggers hold operates and the state is solved again, up to the file's `maxLevels` (default 5). The output keeps the
screen's table (headed `Before schemes:`) and adds a second table (`After schemes:`) with the last level, the status
after the schemes (`SECURE AFTER RAS`, `VIOLATIONS AFTER RAS`, `NOT CONVERGED AFTER RAS` or `RAS LOOP`), the final
loading and voltages, the schemes that operated and one line per action (`ACTION SKIPPED: <reason>` for one that
could not act). Both tables carry the screen's lost load and generation, reference-move and worsened base-case
violation lines for their state. The verdict is judged on the state after the schemes: exit 0 only when the base case and every
contingency are secure after them. The result file adds the summary keys `ras_schemes`, `ras_max_levels`,
`secure_after_ras`, `violations_after_ras`, `not_converged_after_ras` and `ras_loop`, the columns `ras_fired`,
`ras_level`, `post_ras_status`, `post_ras_max_loading_pct`, `post_ras_vmin_pu`, `post_ras_vmax_pu` and
`post_ras_violations` on `outages`, and a `ras_actions` table (`outage,level,scheme,action,kind,element,applied,detail`;
with `--csv`, `<stem>-ras_actions.csv`). A file that cannot be read, or that names an element the case does not have, is
an input error (exit 1). `--ras` on another verb, with `--batched-lanes`, or with an `--outage-dispatch` other than
`reference` exits 2. The file is an input of the run, so the run journal hashes it and `breaking-change validate`
re-runs the job with it. Without `--ras` both verbs are unchanged.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).
