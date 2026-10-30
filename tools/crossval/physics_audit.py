#!/usr/bin/env python3
"""Independent physical-law audit of a GridSim solve/estimate export.

The primary instrument for the physics-conformance work: it rebuilds the bus admittance matrix and
recomputes the network laws in NumPy - a different language and codebase from the C# audit - directly
from the recorded model + state, then cross-checks the exported `physics` block against that
independent recomputation. A pass means the state genuinely satisfies the laws AND the engine's own
audit agrees with an outside recompute.

Laws checked independently (the hard, gate-defining ones):
  1 energy conservation  - sum of net bus injections == reported total loss (solve-shaped exports)
  3 Joule heating        - every branch real loss >= 0 (reported, never gated: a model property)
  6 Kirchhoff current    - injection balance at constrained buses (S = V . conj(Y V))
  8 complex power        - the reactive component of the same balance

What "specified injection" means depends on the export (D-DOC-040, October 2026), read from the exported
`physics.basis`:
  case-dispatch               a solve: the case file's Pg minus the load drawn at the solved voltage, P at every
                              non-slack bus, Q at every bus the solve ended as PQ (D-DOC-041, D-DOC-044; the shared
                              rule is residual_check.injection_balance);
  zero-injection-constraints  an estimate the Feasible gate audited: its hard zero-injection rows (read from the
                              estimate export's measurement set; an estimated-solve export does not carry them, so
                              KCL is not independently checkable there and is reported as such);
  estimate-state              any other estimate: nothing outside the state fixes an injection, so KCL does not
                              apply (laws 6 and 8 must read NotApplicable in the exported block).
Both export kinds are read: gridsim-solve-export/1 (and the estimated-solve export) and gridsim-estimate-export/1
(D-DOC-043: the latter used to stop with a KeyError on "result").

Tolerance (D-DOC-125): by default the one documented rule, BaseMva x 1e-8 MVA (the solver's convergence tolerance on
the case's own base), the same as GridSim's PhysicsLaws.DefaultTolMva and the export's own laws; --tol sets one
absolute line in MVA. (The default was an absolute 1e-4 MVA against the export's 1e-6, so a state between the two
read differently in the two instruments.)

Usage:  python physics_audit.py <solve-or-estimate-export.json> [--tol MVA] [--json]
Exit:   0 pass, 1 fail (state or engine disagree), 3 unreadable export, 77 NOT CHECKED (NumPy missing, or no
        independent comparison applied to this export; nothing was validated). Python's own usage error is 2.
"""
import argparse
import json
import sys

from sidecar_common import EXIT_SKIPPED, default_tol_mva, export_case, require

require(None, "numpy")
import numpy as np  # noqa: E402  (checked just above, with a clear skip when absent)
from residual_check import _f, build_ybus, injection_balance  # noqa: E402

HARD_LAWS = {1, 2, 3, 6, 8}
ZERO_INJECTION_SOURCE = "virtual-zero-injection"   # GridSim's ConstrainedDenseWlsStep.ConstraintSource


def _state(doc, id2i, n):
    """Solved / estimated bus voltages (pu, rad) from either export kind."""
    rows = (doc.get("result") or {}).get("buses")
    if rows is None:
        rows = (doc.get("estimate") or {}).get("buses")
    if rows is None:
        raise ValueError("the export has neither result.buses nor estimate.buses")
    vm = np.ones(n)
    va = np.zeros(n)
    for b in rows:
        i = id2i[b["id"]]
        vm[i] = _f(b.get("vm"), 1.0)
        va[i] = np.radians(_f(b.get("vaDeg")))
    return vm, va


def _zero_injection_kcl(doc, S, id2i):
    """Largest |network injection - constraint value| over the export's hard zero-injection rows (MW / MVAr), with
    the number of rows; None when the document carries no measurement set."""
    items = (doc.get("measurements") or {}).get("items")
    if items is None:
        return None
    worst_p = worst_q = 0.0
    rows = 0
    for m in items:
        if m.get("source") != ZERO_INJECTION_SOURCE or m.get("bus") is None:
            continue
        i = id2i[m["bus"]]
        if m.get("kind") == "PInjMw":
            worst_p = max(worst_p, abs(S.real[i] - _f(m.get("value"))))
            rows += 1
        elif m.get("kind") == "QInjMvar":
            worst_q = max(worst_q, abs(S.imag[i] - _f(m.get("value"))))
            rows += 1
    return worst_p, worst_q, rows


def audit(doc, tol):
    m = doc["model"]
    buses = m["buses"]
    id2i = {b["id"]: i for i, b in enumerate(buses)}
    n = len(buses)
    base = _f(m.get("baseMva"), 100.0)
    res = doc.get("result")              # present in solve-shaped exports only
    phys = doc.get("physics")
    is_estimate_doc = res is None
    basis = (phys or {}).get("basis") or ("estimate-state" if is_estimate_doc else "case-dispatch")
    if tol is None:
        # The documented default, except for an estimate the Feasible gate audited: there the line is the estimator's
        # own gate tolerance (FeasibilityConstraints.AuditTolMva), which the exported laws carry, so both instruments
        # judge the zero-injection rows on the line the estimator promised.
        tol = default_tol_mva(base)
        if basis == "zero-injection-constraints":
            law6 = next((law for law in (phys or {}).get("laws", []) if law.get("number") == 6), None)
            if law6 is not None and _f(law6.get("tolerance"), 0.0) > 0.0:
                tol = _f(law6.get("tolerance"))

    # Independent admittance + injected power S = V (x) conj(Y V) * base (no GridSim math).
    Y = build_ybus(buses, m["branches"], id2i, base)
    vm, va = _state(doc, id2i, n)
    V = vm * np.exp(1j * va)
    S = V * np.conj(Y @ V) * base

    indep = {"sum_injection_mw": float(np.sum(S.real))}
    checks = []          # the independent comparisons that applied: (name, ok)
    notes = []

    # Law 1 - total dissipation (sum of net injections) == reported loss. Needs the reported loss (solve-shaped).
    if res is not None:
        reported_loss = _f(res.get("totalLossMw"))
        energy_mw = abs(indep["sum_injection_mw"] - reported_loss)
        indep.update(energy_mw=energy_mw, reported_loss_mw=reported_loss)
        checks.append(("energy", energy_mw <= tol))
        # Law 3 - every branch real loss is non-negative (reported only: a negative loss is a MODEL passivity
        # artefact, R < 0, reported under Ampere but not gated, matching the C# gate).
        losses = [_f(l.get("pLossMw")) for l in res.get("lineFlows", [])]
        indep["min_branch_loss_mw"] = float(min(losses)) if losses else 0.0
    else:
        notes.append("law 1: no reported loss in an estimate export, not independently checkable")

    # Laws 6 / 8 - the injection balance against what the basis specifies.
    if basis == "case-dispatch":
        bal = injection_balance(doc, S, vm, id2i)
        indep.update(kcl_mw=bal["max_p_mismatch_mw"], s_vi_mvar=bal["max_q_mismatch_pq_mvar"],
                     bus_types=bal["bus_types"], pinned_pv_buses_checked=bal["pinned_pv_buses_checked"],
                     zip_buses=bal["zip_buses"])
        checks.append(("kcl", indep["kcl_mw"] <= tol and indep["s_vi_mvar"] <= tol))
    elif basis == "zero-injection-constraints":
        zi = _zero_injection_kcl(doc, S, id2i)
        if zi is None:
            notes.append("laws 6/8: scored against the estimator's zero-injection rows, which this export does not "
                         "carry; not independently checkable (use estimate-export.json)")
        else:
            indep.update(kcl_mw=zi[0], s_vi_mvar=zi[1], zero_injection_rows=zi[2])
            if zi[2] > 0:
                checks.append(("kcl", zi[0] <= tol and zi[1] <= tol))
    else:  # estimate-state: nothing outside the state fixes an injection
        notes.append("laws 6/8: the estimate fixes no injection outside its own state, so KCL does not apply")

    out = {
        "schema": "gridsim-physics-audit/1",
        "case": export_case(doc),
        "buses": n,
        "tolerance_mva": tol,
        "basis": basis,
        "independent": indep,
        "independent_checks": [c for c, _ in checks],
        "independent_gate_pass": all(ok for _, ok in checks),
        "notes": notes,
    }

    if phys is None:
        out["ok"] = out["independent_gate_pass"]
        out["checked"] = len(checks) > 0
        return out

    exported_gate = bool(phys.get("gatePass"))
    out["exported_gate_pass"] = exported_gate
    # The two gates must agree whenever the independent side checked the laws the exported gate rests on.
    out["gate_agrees"] = (out["independent_gate_pass"] == exported_gate) if checks else True

    # Any hard law the engine reports as Holds must carry a residual within its stated tolerance; under a basis that
    # fixes no injection, laws 6 and 8 must read NotApplicable (never a vacuous Holds).
    laws = phys.get("laws", [])
    bad = [law.get("number") for law in laws
           if law.get("number") in HARD_LAWS and law.get("status") == "Holds"
           and _f(law.get("residual"), 0.0) > _f(law.get("tolerance"), tol) + 1e-12]
    if basis == "estimate-state":
        bad += [law.get("number") for law in laws
                if law.get("number") in (6, 8) and law.get("status") != "NotApplicable"]
    out["exported_holds_within_tol"] = (len(bad) == 0)
    out["exported_law_mismatches"] = bad
    out["ok"] = bool(out["independent_gate_pass"] and out["gate_agrees"] and out["exported_holds_within_tol"])
    out["checked"] = len(checks) > 0
    return out


def main():
    ap = argparse.ArgumentParser(description="Independent physical-law audit of a GridSim export.")
    ap.add_argument("export", help="a gridsim-solve-export or gridsim-estimate-export JSON file")
    ap.add_argument("--tol", type=float, default=None,
                    help="MVA-scale residual tolerance (default: BaseMva x 1e-8, the solver's convergence tolerance)")
    ap.add_argument("--json", action="store_true", help="emit the full result as JSON")
    a = ap.parse_args()
    try:
        with open(a.export, encoding="utf-8") as f:
            doc = json.load(f)
        r = audit(doc, a.tol)
    except (OSError, ValueError, KeyError) as e:
        print(f"physics_audit: cannot read export {a.export}: {e}", file=sys.stderr)
        return 3

    if a.json:
        print(json.dumps(r, indent=2))
    else:
        i = r["independent"]
        print(f"physics audit: {r['case']} ({r['buses']} buses), tol {r['tolerance_mva']:g} MVA, basis {r['basis']}")
        parts = []
        if "energy_mw" in i:
            parts.append(f"energy {i['energy_mw']:.2e} MW")
        if "kcl_mw" in i:
            parts.append(f"KCL {i['kcl_mw']:.2e} MW  S=VI {i['s_vi_mvar']:.2e} MVAr")
        if "min_branch_loss_mw" in i:
            parts.append(f"min branch loss {i['min_branch_loss_mw']:.2e} MW")
        print("  " + ("  ".join(parts) if parts else "(no independent residual applies)"))
        if "bus_types" in i:
            print(f"  reactive balance on {i['bus_types']} bus types; {i['pinned_pv_buses_checked']} PV bus(es) pinned "
                  f"at a reactive limit checked against it; {i['zip_buses']} ZIP load bus(es)")
        for note in r["notes"]:
            print(f"  note: {note}")
        line = f"  independent gate: {'PASS' if r['independent_gate_pass'] else 'FAIL'} ({', '.join(r['independent_checks']) or 'none'})"
        if "exported_gate_pass" in r:
            line += (f"   exported gate: {'PASS' if r['exported_gate_pass'] else 'FAIL'}"
                     f"   agree: {r['gate_agrees']}")
        print(line)
        if r["ok"] and not r["checked"]:
            print("VERDICT: NOT CHECKED - no independent comparison applied to this export (the exported block is "
                  "consistent with its basis)")
        else:
            print("VERDICT:", "PASS" if r["ok"] else "FAIL")
    if not r["ok"]:
        return 1
    return 0 if r["checked"] else EXIT_SKIPPED


if __name__ == "__main__":
    sys.exit(main())
