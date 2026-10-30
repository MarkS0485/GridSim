#!/usr/bin/env python3
"""Independent power-flow RESIDUAL validation of a GridSim solve-export.

Where crossval.py asks "does an independent iterative solver (pandapower) land on
the same voltages?", this asks the more fundamental question: "does GridSim's
solution actually SATISFY the network equations, checked against an admittance
matrix built independently of GridSim's code?"

It rebuilds Ybus from the recorded per-unit branch/shunt data using the standard
MATPOWER pi-model (no GridSim math involved), evaluates the complex power injected
at each bus from GridSim's solved voltages, and diffs against the specified
injection (generation - the load drawn at the solved voltage). It also checks that
every bus still regulating as PV has its implied reactive output within its
aggregate generator Q-limits.

October 2026 (docs-session stream 7):
  * D-DOC-044: the load is the one drawn at the solved voltage, honouring the
    export's ZIP block (it used the nominal Pd / Qd, so a correct solve of a
    voltage-dependent load read as a mismatch).
  * D-DOC-041: the reactive balance is checked at every bus the solve ENDED as
    PQ (from the export's own solve trace), including a PV bus pinned at a
    reactive limit (checked against that limit) and a PV bus with no generator.
  * D-DOC-125: the default tolerance is the one documented rule, BaseMva x 1e-8
    MVA (the solver's convergence tolerance on the case's own base), as in
    GridSim's PhysicsLaws.DefaultTolMva. It was an absolute 1e-6, which failed a
    converged solve on the 10,000 MVA uk base. On a 100 MVA case the two agree
    exactly. --tol still sets one absolute line in MVA.
  * D-DOC-126: the case label is read from meta.Case.
  * NumPy missing: a clear skip, exit 77 (was a traceback).

This validates cases where pandapower's Newton iteration cannot even converge from
a flat start (case300, the large PEGASE nets) - non-convergence of a second solver
is a basin-of-attraction artifact, not evidence against a solution whose residual
is already machine-zero.

Usage: python residual_check.py <solve-export.json> [--tol MVA] [--out report.json]
Exit: 0 within tol, 1 mismatch, 3 unreadable, 77 NumPy missing (nothing validated).
"""
import argparse
import json
import sys

from sidecar_common import (default_tol_mva, export_case, load_at_voltage, pinned_q_limits, require,
                            solved_bus_types)

np, = require(None, "numpy")


def _f(v, default=0.0):
    """Coerce a possibly-stringified numeric JSON field to float."""
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


def build_ybus(buses, branches, id2i, base):
    n = len(buses)
    Y = np.zeros((n, n), complex)
    for e in branches:
        # An out-of-service branch carries no admittance (GridSim skips it in the solve). Exports written
        # before the field existed omit it, which means in service.
        if e.get("inService", True) is False:
            continue
        f, t = id2i[e["from"]], id2i[e["to"]]
        z = complex(_f(e.get("r")), _f(e.get("x")))
        if z == 0:
            continue
        y = 1.0 / z
        bc = 1j * _f(e.get("b")) / 2.0
        tap = _f(e.get("tap"))
        shift = np.radians(_f(e.get("phaseShiftDeg")))
        T = (tap if tap != 0 else 1.0) * np.exp(1j * shift)
        Y[f, f] += (y + bc) / (abs(T) ** 2)
        Y[t, t] += y + bc
        Y[f, t] += -y / np.conj(T)
        Y[t, f] += -y / T
    for i, b in enumerate(buses):
        Y[i, i] += complex(_f(b.get("gs")), _f(b.get("bs"))) / base
    return Y


def injection_balance(doc, S, vm, id2i):
    """The KCL balance of a solved state against its specified injections, shared with physics_audit.py.

    S is the network's injected complex power per bus (MVA, model bus order), vm the solved magnitudes (pu).
    Returns a dict: the largest real mismatch over the non-slack buses, the largest reactive mismatch over the buses
    the solve ended as PQ, the implied generator Q at every bus, and what was used to decide (types, ZIP, pinned).

    The specified injection at bus i is sum(Pg) - P_load(V_i) and sum(Qg) - Q_load(V_i), with the load drawn at the
    solved voltage (D-DOC-044). At a bus declared PV that the solve ended as PQ while it has generators (pinned at a
    reactive limit, D-DOC-041) the reactive specification is that limit (the aggregate Qmax or Qmin, as the solve's
    qLimitEvents record) minus the load; when the export does not record which limit, the nearer one is used."""
    m = doc["model"]
    buses = m["buses"]
    n = len(buses)
    Pg = np.zeros(n)
    Qg = np.zeros(n)
    qmax = np.zeros(n)
    qmin = np.zeros(n)
    isg = np.zeros(n, bool)
    for g in m.get("generators", []):
        # D-DOC-067: the solve export lists the whole fleet with "inService"; an out-of-service unit injects nothing (the
        # solve left it out). Older exports omit the field and list only in-service units.
        if g.get("inService", True) is False:
            continue
        i = id2i[g["bus"]]
        Pg[i] += _f(g.get("pg"))
        Qg[i] += _f(g.get("qg"))
        qmax[i] += _f(g.get("qmax"), 9e3)
        qmin[i] += _f(g.get("qmin"), -9e3)
        isg[i] = True

    Pl = np.zeros(n)
    Ql = np.zeros(n)
    for i, b in enumerate(buses):
        Pl[i], Ql[i] = load_at_voltage(b, float(vm[i]))
    zip_buses = sum(1 for b in buses if b.get("zip"))

    solved, from_trace = solved_bus_types(doc)
    pinned = pinned_q_limits(doc)
    declared = [str(b["type"]).lower() for b in buses]
    typ = [solved.get(b["id"], declared[i]) for i, b in enumerate(buses)]

    dP = S.real - (Pg - Pl)
    non_slack = [i for i in range(n) if typ[i] != "slack"]
    max_p = float(np.max(np.abs(dP[non_slack]))) if non_slack else 0.0

    max_q = 0.0
    q_buses = 0
    pinned_checked = 0
    for i in range(n):
        if typ[i] != "pq":
            continue
        q_buses += 1
        if declared[i] == "pv" and isg[i]:
            # Pinned at a reactive limit: the solve fixed its injection at that limit minus the load.
            pinned_checked += 1
            at = pinned.get(buses[i]["id"])
            to_max = abs(S.imag[i] - (qmax[i] - Ql[i]))
            to_min = abs(S.imag[i] - (qmin[i] - Ql[i]))
            dq = to_max if at == "max" else to_min if at == "min" else min(to_max, to_min)
        else:
            dq = abs(S.imag[i] - (Qg[i] - Ql[i]))
        max_q = max(max_q, float(dq))

    return {
        "max_p_mismatch_mw": max_p,
        "max_q_mismatch_pq_mvar": max_q,
        "q_balance_buses": q_buses,
        "pinned_pv_buses_checked": pinned_checked,
        "bus_types": "solved (from the export's solve trace)" if from_trace else "declared (no solve trace)",
        "zip_buses": zip_buses,
        "implied_qg": S.imag + Ql,
        "types": typ,
        "isg": isg,
        "qmin": qmin,
        "qmax": qmax,
    }


def validate(doc, tol):
    m, res = doc["model"], doc["result"]
    buses = m["buses"]
    id2i = {b["id"]: i for i, b in enumerate(buses)}
    n = len(buses)
    base = _f(m.get("baseMva"), 100.0)
    if tol is None:
        tol = default_tol_mva(base)

    Y = build_ybus(buses, m["branches"], id2i, base)

    vm = np.ones(n)
    va = np.zeros(n)
    for b in res["buses"]:
        i = id2i[b["id"]]
        vm[i] = _f(b.get("vm"), 1.0)
        va[i] = np.radians(_f(b.get("vaDeg")))
    V = vm * np.exp(1j * va)
    S = V * np.conj(Y @ V) * base   # injected MVA per bus

    bal = injection_balance(doc, S, vm, id2i)
    maxP, maxQ = bal["max_p_mismatch_mw"], bal["max_q_mismatch_pq_mvar"]

    # A bus still regulating as PV must hold its implied reactive output inside the aggregate limits (with the
    # solver's own 1e-3 MVAr switching slack, NewtonShell.QLimitTolMva).
    tolq = max(1e-3, tol)
    Qimp, typ, isg, qmin, qmax = bal["implied_qg"], bal["types"], bal["isg"], bal["qmin"], bal["qmax"]
    viol = [{"bus": buses[i]["id"], "impliedQg": round(float(Qimp[i]), 2),
             "qmin": round(float(qmin[i]), 2), "qmax": round(float(qmax[i]), 2)}
            for i in range(n) if isg[i] and typ[i] == "pv"
            and (Qimp[i] > qmax[i] + tolq or Qimp[i] < qmin[i] - tolq)]

    ok = maxP <= tol and maxQ <= tol and not viol
    return {
        "schema": "gridsim-residual-check/1",
        "case": export_case(doc),
        "buses": n,
        "tolerance_mva": tol,
        "max_p_mismatch_mw": maxP,
        "max_q_mismatch_pq_mvar": maxQ,
        "q_balance_buses": bal["q_balance_buses"],
        "pinned_pv_buses_checked": bal["pinned_pv_buses_checked"],
        "bus_types": bal["bus_types"],
        "zip_buses": bal["zip_buses"],
        "pv_qlimit_violations": len(viol),
        "violations": viol[:10],
        "within_tolerance": bool(ok),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("export")
    ap.add_argument("--tol", type=float, default=None,
                    help="MVA tolerance (default: BaseMva x 1e-8, the solver's convergence tolerance on the case's base)")
    ap.add_argument("--out")
    args = ap.parse_args()
    try:
        with open(args.export, encoding="utf-8") as f:
            doc = json.load(f)
        if "result" not in doc or "model" not in doc:
            raise ValueError(f"not a gridsim solve export (schema={doc.get('schema')!r}); "
                             "an estimate export is checked by se_residual_check.py / physics_audit.py")
    except Exception as e:  # noqa: BLE001
        print(f"residual_check: cannot read export: {e}", file=sys.stderr)
        return 3
    rep = validate(doc, args.tol)
    text = json.dumps(rep, indent=2)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(text)
    print(text)
    return 0 if rep["within_tolerance"] else 1


if __name__ == "__main__":
    sys.exit(main())
