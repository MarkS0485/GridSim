---
name: gridsim-construct
description: Use when constructing a plausible (synthetic) power network from public sources with the GridSim tools. Stub - the full method is pending review; follow the method resource and the rules below.
---

# Constructing a plausible network (stub)

The full construction method is pending review. Until it ships, read the resource `gridsim://plugin/construct-method`
and keep to these rules:

- Build in rounds. After every round: `case_validate`, `solve_pf`, `physics_check`. A round that fails a gate is fixed
  before the next one.
- Never tune values to make the solver converge or a study pass. A modest overload may be real: report it.
- Every value is cited (document id, version, section) or tagged `Synthetic` / `CuratedPlaceholder` with `[VERIFY]`.
  Derive line R, X and B with `line_parameters` from a stated geometry.
- Label inferred elements as inferred, and keep them to the minimum the network needs to be connected and solvable.
- Never name, locate or attribute real facilities or customers. Demand is aggregate.

## Citing evidence (every answer)

Cite the build tag, the case hash, the result hash and the weakest provenance tag (a constructed case is `Synthetic`
at best). Say "verified solver, unvalidated case".

## Refusals

A refused verb, flag or location is not available here. Do not work around it.
