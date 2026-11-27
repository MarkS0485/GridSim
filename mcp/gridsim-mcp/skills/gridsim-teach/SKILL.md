---
name: gridsim-teach
description: Use when teaching or learning power-system basics with the GridSim tools - per-unit, PV and PQ buses, reactive limits, N-1 security, fault level, inertia and RoCoF - by building a small network, then breaking it, with every step explained and GridSim's evidence cited.
---

# Teaching with GridSim: build it, then break it

The student learns by watching GridSim answer, not by reading your answer. Each step: say what you will do and why,
make the call (with a short `reasoning`), show what GridSim returned, then explain it. Show your working (the per-unit
arithmetic, the expected direction of a change) before the result, so a wrong expectation is visible.

## Lesson shape

1. **Start small.** `case_load case9` (or `case14`, or a feeder from `gridsim://plugin/starter-cases`), or `case_new`
   and a few `case_edit add` calls. Name every assumption.
2. **Solve and check.** `solve_pf`, then `physics_check`. Read the laws that hold, in plain words.
3. **Look inside the solve.** `explain_solve`: iterations, how the mismatch falls (quadratically near the answer for
   Newton-Raphson), PV->PQ switches when a generator hits its reactive limit, the worst bus. Jacobian for small cases.
4. **Change one thing.** `case_save`, `case_edit`, solve again, then `diff_cases` and `diff_results`: only what moved
   is discussed.
5. **Break it.** `break_it` (trip a line or a generator, overload, remove the slack, island a bus, apply a fault).
   GridSim fails loud: read its message, then the matching Troubleshooting entry it returns, and explain the physics.

## Lessons (one prompt each)

- **Per-unit** (`gridsim://docs/per-unit`): base MVA and base kV, Zbase = kV^2 / MVA; convert one line's ohms to per
  unit by hand, then compare with `line_parameters` (per-unit flags) and the case.
- **PV vs PQ**: a PV bus holds |V| and P, a PQ bus holds P and Q; the slack balances. Show `busSpec` types in
  `explain_solve`.
- **Reactive limits**: lower a generator's Qmax with `case_edit`, re-solve; `explain_solve` shows the PV->PQ switch
  and the voltage that then drops.
- **N-1**: `study nminus1`; explain each violation; then `break_it` the worst outage and read the result.
- **Fault level**: `study iec60909` (or `fault`) at a bus; relate the current to the Thevenin impedance; show how a
  stronger source raises it.
- **Inertia and RoCoF**: RoCoF = dP * f0 / (2 * sum(H * S)) at the first instant; `run_verb` `inertia-placement`
  or `stability` on a case with machine data (`case9` carries WSCC H and MBase). Model list:
  `gridsim://docs/dynamic-models`.

## Citing evidence (every answer)

Cite the build tag and GridSim version, the case hash, the result hash and the weakest provenance tag of the inputs
(a hand-built case is `Synthetic`; a placeholder value is `CuratedPlaceholder [VERIFY]`). Say "verified solver,
unvalidated case". Point to the study record entry (`record_seq`) so the student can replay it.

## Refusals

A refused verb, flag or location is not available here. Do not work around it, and do not present a hand calculation
as GridSim's result. Explain that it is not offered, and carry on with what is.
