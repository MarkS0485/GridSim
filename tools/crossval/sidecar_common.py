#!/usr/bin/env python3
"""Shared helpers for the GridSim cross-validation sidecars (October 2026, docs-session stream 7).

Standard library only, so every sidecar can import it BEFORE it checks for NumPy / SciPy / pandapower, and so a
missing package is always reported the same way (a clear message and exit 77, "skipped", never a traceback).

What lives here, and why one place:

  * EXIT_SKIPPED / require()   D-DOC-127: a missing dependency is an ENVIRONMENT skip, exit 77 (the automake / CTest
                               "skipped" code), with a message naming the package and the interpreter that lacked it.
                               Exit 77 means nothing was validated and is never a pass. Before, crossval_estimation.py
                               exited 2 (which Python also uses for a usage error), and se_residual_check.py and
                               crossval_harmonics.py crashed with a traceback (exit 1, read as a mismatch).
  * export_case()              D-DOC-126: the case label. The exports serialise their meta record with .NET's default
                               (PascalCase) names, so the key is "Case"; three sidecars read "case" and printed "?".
  * load_at_voltage()          D-DOC-044: the load a bus actually draws at its solved voltage, honouring the export's
                               ZIP block (P = Pd (ap V^2 + bp V + cp), Q = Qd (aq V^2 + bq V + cq)) exactly as GridSim's
                               LoadAtVoltage does. The sidecars used the nominal Pd / Qd, so a correct solve of a
                               voltage-dependent load read as a mismatch.
  * solved_bus_types()         D-DOC-041: the bus types the solve ENDED with (a PV bus pinned at a reactive limit, or
    pinned_q_limits()          with no generator, is solved as PQ), read from the export's own solve trace, and the
                               limit each pinned bus was held at. The reactive balance is checked on those, not on the
                               declared types.
  * default_tol_mva()          D-DOC-125: the one documented audit tolerance, BaseMva * 1e-8 MVA (the power-flow
                               convergence tolerance, 1e-8 pu, on the case's own base), the same rule as GridSim's
                               PhysicsLaws.DefaultTolMva. An explicit --tol overrides it.
"""
import importlib
import os
import sys

# The conventional "skipped" exit code (automake / CTest): a dependency is absent, so nothing was validated.
EXIT_SKIPPED = 77

# The power-flow convergence tolerance in per-unit (GridSim's PowerFlowOptions.Tolerance default and
# PhysicsLaws.DefaultTolerancePu; a C# test pins this file's value to the same number).
DEFAULT_TOLERANCE_PU = 1e-8


def require(tool, *modules):
    """Import each named module or stop with a clear skip (exit 77) naming the first one missing.

    `tool` is the sidecar's name for the message (None: the script that is running, so a helper module imported by
    another sidecar names the sidecar the user ran). Returns the imported modules in order, so a caller can write
    `np, = require("physics_audit", "numpy")`.
    """
    loaded = missing(tool, *modules)
    if loaded is None:
        sys.exit(EXIT_SKIPPED)
    return loaded


def missing(tool, *modules):
    """The non-exiting form of require(): import each module, or print the same skip message and return None, so a
    caller that returns its own exit code from main() can `return EXIT_SKIPPED` itself. Returns the modules otherwise."""
    if tool is None:
        tool = os.path.splitext(os.path.basename(sys.argv[0] or "sidecar"))[0] or "sidecar"
    loaded = []
    for name in modules:
        try:
            loaded.append(importlib.import_module(name))
        except ImportError as e:  # the package (or one it needs) is not installed in this interpreter
            top = name.split(".")[0]
            print(f"{tool}: SKIPPED (exit {EXIT_SKIPPED}): the Python package '{top}' is not installed in "
                  f"{sys.executable} ({e}). Nothing was validated. Install it (pip install {top}) or run this "
                  f"script with an interpreter that has it.", file=sys.stderr)
            return None
    return loaded


def export_case(doc):
    """The case label of an export (D-DOC-126): meta.Case as written, meta.case for an older or hand-made
    document, else the model's own name, else '?'."""
    meta = doc.get("meta") or {}
    for key in ("Case", "case"):
        value = meta.get(key)
        if value:
            return str(value)
    name = (doc.get("model") or {}).get("name")
    return str(name) if name else "?"


def _f(v, default=0.0):
    """Coerce a possibly-missing numeric JSON field to float."""
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


def load_at_voltage(bus, vm):
    """(P MW, Q MVAr) the bus load draws at voltage magnitude vm (pu), D-DOC-044.

    A bus with no "zip" block is constant power (Pd, Qd), exactly as before. With one, the polynomial is applied as
    GridSim's ZipLoad.PMultiplier / QMultiplier do (multiplier = a V^2 + b V + c; missing coefficients take the model
    defaults a = b = 0, c = 1)."""
    pd = _f(bus.get("pd"))
    qd = _f(bus.get("qd"))
    z = bus.get("zip")
    if not z:
        return pd, qd
    pm = _f(z.get("ap")) * vm * vm + _f(z.get("bp")) * vm + _f(z.get("cp"), 1.0)
    qm = _f(z.get("aq")) * vm * vm + _f(z.get("bq")) * vm + _f(z.get("cq"), 1.0)
    return pd * pm, qd * qm


def solved_bus_types(doc):
    """Bus id -> 'pq' / 'pv' / 'slack' as the solve ENDED (D-DOC-041).

    Read from the export's solve trace: the last reactive-limit pass's effectiveTypes, indexed like solve.busSpec
    (whose rows carry the bus Id). A document without that trace (an estimate export, a sweep solve, an older file)
    falls back to the model's declared types, and the second value says which was used."""
    model = doc.get("model") or {}
    declared = {b["id"]: str(b["type"]).lower() for b in model.get("buses", [])}
    solve = doc.get("solve") or {}
    passes = solve.get("passes") or []
    spec = solve.get("busSpec") or []
    if passes and spec:
        eff = passes[-1].get("effectiveTypes") or []
        if len(eff) == len(spec):
            ids = [row.get("Id", row.get("id")) for row in spec]
            if all(i in declared for i in ids):
                return {bid: str(t).lower() for bid, t in zip(ids, eff)}, True
    return declared, False


def pinned_q_limits(doc):
    """Bus id -> 'max' / 'min': the reactive limit the solve last pinned each bus at (from solve.qLimitEvents).

    Only meaningful for a bus that solved_bus_types() reports as 'pq' while the model declares it 'pv'."""
    out = {}
    for ev in ((doc.get("solve") or {}).get("qLimitEvents") or []):
        bid = ev.get("BusId", ev.get("busId"))
        at = str(ev.get("PinnedAt", ev.get("pinnedAt", ""))).lower()
        if bid is not None and at in ("max", "min"):
            out[bid] = at
    return out


def default_tol_mva(base_mva):
    """The one documented default audit tolerance (D-DOC-125): BaseMva * 1e-8 MVA."""
    return _f(base_mva, 100.0) * DEFAULT_TOLERANCE_PU
