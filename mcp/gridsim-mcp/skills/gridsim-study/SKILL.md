---
name: gridsim-study
description: Use when a planner studies a power network with the GridSim tools - import or build a case, validate, physics check, contingency and fault studies, a declarative study case, and a GridSim-rendered report - and when writing up such a study with its evidence.
---

# Studying a network with GridSim (planner workflow)

GridSim's solver is verified (to machine precision against independent references). A case you import or build, and
any study on it, is not validated against measurements. Say "verified", never "validated", of a result.

## Workflow

1. **Preflight.** `preflight`: note the GridSim version, the commit, the build tag (`[RELEASE]` or
   `[UNSUPPORTED BUILD]`) and the compliance verdict, which is reported separately.
2. **Bring the model in.**
   - A model in another format: `import_model` (MATPOWER, PSS/E RAW, RAWX, UCTE-DEF, CGMES, JSON, PowerFactory DGS,
     IEC 61850 SCL). Report every `note:` line it returns (what the target cannot hold, or what was dropped) and the
     `round trip:` statement. DGS and SCL are one-way imports: say so.
   - A shipped case: `case_load` (see the resource `gridsim://plugin/starter-cases` for bus counts and licence need).
   - A new network: `case_new`, then `case_edit` op `add`. Derive line R, X and B with `line_parameters` from a stated
     geometry instead of guessing; follow the construction method (`gridsim-construct`) for a plausible network.
3. **Validate.** `case_validate` (schema and semantic checks), then `solve_pf` and `physics_check`. A physics-check
   FAIL names the law broken: report it, do not explain it away.
4. **Study.** `study` (nminus1, nminus2, iec60909, fault, dcopf, acopf-full, scopf, stability) or `run_verb` for other
   allowlisted verbs. Exit 3 is a verdict (NOT SECURE, DIVERGED, INFEASIBLE), still a result: report it.
5. **Repeatable studies.** For variations, scenarios and dated changes, write a `gridsim-studycase/1` (or
   `gridsim-planning/1`) file and run it with `study_case_run`. Cite the run manifest's per-job result hashes.
6. **Report.** `report_render` on the result id: a hash-bound Markdown or HTML report. Narrate it; do not retype its
   numbers by hand.
7. **Show what changed.** `case_save` before an edit, then `diff_cases`; `diff_results` for what moved.
8. **Keep the record.** Pass a short `reasoning` with each call; finish with `record_export`.

## Citing evidence (every answer)

Every claim about a number cites, from the evidence block of the call it rests on:

- the **build tag** and GridSim version (`[RELEASE]` or `[UNSUPPORTED BUILD]`);
- the **case hash** and the **result hash**;
- the **weakest provenance tag** of the inputs the number depends on (for example `CuratedPlaceholder [VERIFY]`
  outranks `Recorded`; a built or imported case with assumed values is `Synthetic` at best).

Write "verified solver, unvalidated case" in the limits line. One footer line of limits is enough.

## Refusals

A verb, flag or location that the server refuses is not available here. Do not work around it (no other verb to the
same end, no copying files to dodge a path rule, no hand calculation presented as GridSim's). Say what was refused
and stop that line of work.
