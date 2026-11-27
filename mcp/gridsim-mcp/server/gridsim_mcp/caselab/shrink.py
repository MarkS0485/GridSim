"""The greedy search for a minimal reproducer.

Start from the case that showed a finding and try every reduction in a fixed order (model.reductions): merge a leaf
bus into its neighbour (its load and shunt move with it), drop a generator, drop a branch that closes a loop, set a
transformer to nominal tap, drop a shunt. The first reduction that still shows the same finding (same class, mode, tool
and signature) is kept and the search starts again from the smaller case. It stops when no single reduction keeps the
finding (the case is then 1-minimal under these reductions) or when the evaluation budget is spent.

Every candidate goes through the same generator gates as a generated case (validate, physics-check, plausible
voltages) before it is compared: a reduction that makes the case implausible or unsolvable is skipped, never kept.
"""

from . import model


def shrink(case, predicate, budget=40, log=None):
    """(smallest case, steps applied, evaluations, minimal) for ``predicate(case) -> bool``.

    ``predicate`` runs the candidate (gates and the compare) and says whether the finding is still shown; it is never
    called on the starting case. ``minimal`` is True when the search ended because no reduction applies."""
    current, trail, evals = case, [], 0
    while True:
        improved = False
        for step in model.reductions(current):
            try:
                cand = model.apply_step(current, step)
            except model.StepRefused:
                continue
            if model.size(cand) >= model.size(current):
                continue
            if evals >= budget:
                return current, trail, evals, False
            evals += 1
            ok = bool(predicate(cand))
            if log:
                log(step, ok)
            if ok:
                current = cand
                trail.append(step)
                improved = True
                break
        if not improved:
            return current, trail, evals, True
