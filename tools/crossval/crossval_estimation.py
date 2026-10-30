#!/usr/bin/env python3
"""Cross-validate a GridSim WLS state estimate against pandapower.estimation.

Reads a GridSim estimate-export JSON (schema `gridsim-estimate-export/1`), rebuilds the identical
network as a ppc (reusing crossval.py's build_ppc), feeds pandapower the IDENTICAL measurement set,
runs pandapower's WLS estimator, and diffs the two estimated states. Like crossval.py it is
deliberately dumb: it does not trust GridSim's math - it re-estimates from the recorded inputs with
a named industry tool.

Sign convention: pandapower bus P/Q measurements are CONSUMPTION-positive (load convention), while
GridSim measures net INJECTION (generation - demand, matching PowerFlowResult.PInjMw). The flip
lives here, in one place (`INJECTION_SIGN`). Branch-flow measurements are into-the-branch at the
measured end on both sides, so they pass through unflipped.

Modes:
  default     - diff estimated Vm/Va (slack-aligned) over the measurements the estimate was
                finally computed from (i.e. after GridSim's bad-data removals).
  --bad-data  - feed pandapower the ORIGINAL set (including any gross errors), run its
                chi2 analysis + largest-normalized-residual removal, and check that the two
                tools AGREE ON WHETHER anything was removed (counts_agree). It does NOT check
                that they removed the same measurement: GridSim's first removal is reported
                (gridsim_first_removed) for a human to compare, not compared (R103).

Measurement kinds: VmPu, VaDeg (pandapower "va", degrees), PInjMw / QInjMvar, PFlowMw / QFlowMvar.
The branch current kinds IMagPu / IAngDeg are refused with exit 3 (pandapower's current measurements
are in kA on its own base, and that conversion is not validated here); se_residual_check.py checks
them independently. Any unknown kind is also refused with exit 3, naming it (R308).

Usage:
    python crossval_estimation.py <estimate-export.json | run-dir> [--tol-vm 1e-6] [--tol-va 1e-5]
                                  [--bad-data] [--out diff.json]

Why the defaults are still --tol-vm 1e-6 pu and --tol-va 1e-5 deg (N248, October 2026, recorded rather than changed):
the two estimates are each the stopping point of an iteration, not a closed form. pandapower's WLS stops when its state
update falls below `tolerance=1e-8` (see run_estimation), GridSim's at its own convergence tolerance, so the two
states can differ by up to about those stopping tolerances, not by rounding; and a Feasible GridSim estimate with
zero-injection constraints is compared against a pandapower estimate that does not model them (the N245 remainder).
A machine-epsilon default (as N243 gave the power-flow cross-check) needs a like-for-like comparison first: both
estimators driven to the same fixed point (pandapower's tolerance tightened to the limit its iteration reaches, the
zero-injection rows modelled or the case refused) and the residual difference measured on every case the cross-check
covers. Tightening the number without that measurement would turn agreeing estimates into false FAILs, so it is left
to the next sidecar measurement batch (N248 in the LF1 notes). Explicit --tol-vm / --tol-va still override.

Exit codes: 0 = within tol, 1 = mismatch, 3 = unreadable export or an unsupported measurement kind,
4 = pandapower estimation crashed, 77 = NumPy or pandapower absent (nothing validated). D-DOC-127 (October 2026):
the missing-dependency skip was exit 2, which Python also uses for a usage error, so a skip and a bad command line
read the same; it is now 77, the code every sidecar and `gridsim crossval` use for "skipped". Python's own usage
error stays 2.
"""
import argparse
import json
import os
import sys

from crossval import build_ppc, _resolve_from_ppc
from sidecar_common import export_case, require

SCHEMA = "gridsim-estimate-export/1"
INJECTION_SIGN = -1.0  # GridSim injection (gen - load)  ->  pandapower bus measurement (load convention)


def load_export(path):
    if os.path.isdir(path):
        path = os.path.join(path, "estimate-export.json")
    with open(path, "r", encoding="utf-8") as f:
        doc = json.load(f)
    if doc.get("schema") != SCHEMA:
        raise ValueError(f"not a gridsim estimate-export (schema={doc.get('schema')!r})")
    return doc


def convert_net(model):
    net = None
    from_ppc = _resolve_from_ppc()
    # build_ppc returns (ppc, id_to_row, info) since N243 (1.95.197); this call still unpacked two values, so every
    # run failed with "too many values to unpack (expected 2)", exit 4 (found by DOC-8 running the sidecars). Only the
    # ppc is used here.
    ppc = build_ppc(model)[0]
    # Normalize every bus's base kV before conversion. The whole comparison lives in per-unit /
    # MW, where base kV is inert (MATPOWER's pu model never uses it), but from_ppc uses vn
    # MISMATCH to route ratio-1 branches into `impedance` elements - which
    # pandapower.create_measurement cannot address (case118 has two such branches). With a
    # uniform vn every branch lands as a measurable line/trafo and the pu network is unchanged.
    ppc["bus"][:, 9] = 110.0
    try:
        net = from_ppc(ppc, f_hz=50.0)
    except TypeError:
        net = from_ppc(ppc)
    return net


def bus_index_map(net):
    """pandapower bus index by original bus id (from_ppc stores the ppc bus number in name)."""
    out = {}
    for idx in net.bus.index:
        name = net.bus.at[idx, "name"]
        try:
            out[int(float(name))] = idx
        except (TypeError, ValueError):
            out[idx] = idx
    return out


def branch_element_map(model, net, ppbus):
    """model branch order -> ('line'|'trafo', element index, flipped) in the converted net.

    from_ppc emits lines/trafos in ppc branch order within each table, so walking the model's
    branches and consuming matching net rows in order resolves parallels deterministically.
    `flipped` records a trafo whose hv/lv ended up opposite the ppc from/to (from_ppc orients
    trafos hv->lv), so 'from'-end measurements land on the correct physical terminal.
    """
    import pandas as pd  # noqa: F401  (pandapower ships it)

    line_rows = list(net.line.index)
    trafo_rows = list(net.trafo.index)
    mapping = []
    li = ti = 0
    for e in model["branches"]:
        f, t = ppbus[e["from"]], ppbus[e["to"]]
        placed = False
        if li < len(line_rows):
            row = line_rows[li]
            if net.line.at[row, "from_bus"] == f and net.line.at[row, "to_bus"] == t:
                mapping.append(("line", row, False))
                li += 1
                placed = True
        if not placed and ti < len(trafo_rows):
            row = trafo_rows[ti]
            hv, lv = net.trafo.at[row, "hv_bus"], net.trafo.at[row, "lv_bus"]
            if (hv, lv) == (f, t):
                mapping.append(("trafo", row, False))
                ti += 1
                placed = True
            elif (hv, lv) == (t, f):
                mapping.append(("trafo", row, True))
                ti += 1
                placed = True
        if not placed:
            raise ValueError(f"cannot map model branch {e['from']}->{e['to']} onto the converted net")
    return mapping


def resolve_branch_ordinal(model, m):
    """A measurement's (from,to[,circuit]) -> model branch ordinal, mirroring GridSim
    (including the '#k' ordinal-circuit form for unnamed parallel branches)."""
    circuit = m.get("circuit")
    if circuit and circuit.startswith("#"):
        k = int(circuit[1:])
        e = model["branches"][k]
        if e["from"] != m["from"] or e["to"] != m["to"]:
            raise ValueError(f"circuit {circuit} endpoints differ from measurement {m['from']}->{m['to']}")
        return k
    hits = [k for k, e in enumerate(model["branches"])
            if e["from"] == m["from"] and e["to"] == m["to"]
            and (circuit is None or (e.get("name") or "") == circuit)]
    if len(hits) != 1:
        raise ValueError(f"measurement branch {m['from']}->{m['to']} resolved to {len(hits)} branches")
    return hits[0]


# The kinds this comparison feeds to pandapower. R308: VaDeg rows (written by `estimate --phaseangle`) used to
# fall into the branch-flow branch and crash with a KeyError (no from/to), so phase-angle-augmented estimates could
# never be cross-validated; current kinds did the same.
SUPPORTED_KINDS = ("VmPu", "VaDeg", "PInjMw", "QInjMvar", "PFlowMw", "QFlowMvar")
BRANCH_FLOW_KINDS = ("PFlowMw", "QFlowMvar")


def unsupported_kinds(measurements):
    """The measurement kinds in the set that this comparison cannot feed to pandapower (sorted, unique)."""
    return sorted({m.get("kind") for m in measurements if m.get("kind") not in SUPPORTED_KINDS}, key=str)


def add_measurements(net, model, measurements, ppbus, branch_map):
    import pandapower as pp

    for m in measurements:
        kind, val, std = m["kind"], float(m["value"]), float(m["sigma"])
        if kind == "VmPu":
            pp.create_measurement(net, "v", "bus", val, std, element=ppbus[m["bus"]])
        elif kind == "VaDeg":
            # pandapower's bus voltage-angle measurement, in degrees like GridSim's VaDeg row.
            pp.create_measurement(net, "va", "bus", val, std, element=ppbus[m["bus"]])
        elif kind in ("PInjMw", "QInjMvar"):
            mt = "p" if kind == "PInjMw" else "q"
            pp.create_measurement(net, mt, "bus", INJECTION_SIGN * val, std, element=ppbus[m["bus"]])
        elif kind in BRANCH_FLOW_KINDS:
            mt = "p" if kind == "PFlowMw" else "q"
            etype, elem, flipped = branch_map[resolve_branch_ordinal(model, m)]
            measured_from = m.get("end") != "To"
            if etype == "line":
                side = "from" if measured_from else "to"
            else:
                # trafo sides are hv/lv; account for a conversion that swapped orientation
                side = ("hv" if measured_from else "lv") if not flipped else ("lv" if measured_from else "hv")
            pp.create_measurement(net, mt, etype, val, std, element=elem, side=side)
        else:
            raise ValueError(f"unsupported measurement kind {kind!r}")   # main() refuses these up front


def slack_offset(model, doc, net):
    slack_ids = [b["id"] for b in model["buses"] if str(b.get("type", "")).lower() == "slack"]
    if not slack_ids:
        return 0.0
    sid = slack_ids[0]
    gs = next((b["vaDeg"] for b in doc["estimate"]["buses"] if b["id"] == sid), 0.0)
    ppbus = bus_index_map(net)
    return float(net.res_bus_est.at[ppbus[sid], "va_degree"]) - gs


def run_estimation(net):
    from pandapower.estimation import estimate
    ok = estimate(net, algorithm="wls", init="flat", tolerance=1e-8, maximum_iterations=50)
    if not ok:
        raise RuntimeError("pandapower WLS did not converge")


def diff_states(doc, net, tol_vm, tol_va):
    model = doc["model"]
    ppbus = bus_index_map(net)
    offset = slack_offset(model, doc, net)

    rows = []
    max_dvm = max_dva = 0.0
    for b in doc["estimate"]["buses"]:
        pidx = ppbus[b["id"]]
        vm_pp = float(net.res_bus_est.at[pidx, "vm_pu"])
        va_pp = float(net.res_bus_est.at[pidx, "va_degree"]) - offset
        dvm = abs(vm_pp - b["vm"])
        dva = abs(va_pp - b["vaDeg"])
        max_dvm = max(max_dvm, dvm)
        max_dva = max(max_dva, dva)
        rows.append({"bus": b["id"], "vm_gridsim": b["vm"], "vm_pandapower": vm_pp, "dvm": dvm,
                     "va_gridsim": b["vaDeg"], "va_pandapower": va_pp, "dva_deg": dva})
    return {
        "schema": "gridsim-crossval-estimation/1",
        "tool": "pandapower.estimation",
        "case": export_case(doc),   # D-DOC-126: one reader for the case label (meta.Case, then meta.case, then the model name)
        "buses_compared": len(rows),
        "max_dvm_pu": max_dvm,
        "max_dva_deg": max_dva,
        "within_tolerance": bool(max_dvm <= tol_vm and max_dva <= tol_va),
        "rows": rows,
    }


def compare_bad_data(doc, net, model):
    """Both tools must finger the same gross error(s) from the ORIGINAL measurement set."""
    from pandapower.estimation import chi2_analysis, remove_bad_data

    removed_gs = doc.get("removed", [])
    chi2_reject = bool(chi2_analysis(net, init="flat"))
    n_meas_before = len(net.measurement)
    ok = remove_bad_data(net, init="flat", rn_max_threshold=3.0)
    dropped = n_meas_before - len(net.measurement)

    agree = (len(removed_gs) == 0) == (dropped == 0)
    report = {
        "schema": "gridsim-crossval-estimation-baddata/1",
        "gridsim_removed": len(removed_gs),
        "gridsim_chi2_failed_initially": len(removed_gs) > 0,
        "pandapower_chi2_rejects": chi2_reject,
        "pandapower_removed": int(dropped),
        "pandapower_converged_after": bool(ok),
        "counts_agree": bool(agree),
    }
    if removed_gs and dropped:
        # Identify the pandapower-dropped measurement and compare location/kind.
        gs = removed_gs[0]["measurement"]
        report["gridsim_first_removed"] = {k: gs.get(k) for k in ("kind", "bus", "from", "to", "end")}
    return report


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("export")
    ap.add_argument("--tol-vm", type=float, default=1e-6)
    ap.add_argument("--tol-va", type=float, default=1e-5)
    ap.add_argument("--bad-data", action="store_true")
    ap.add_argument("--out")
    args = ap.parse_args()

    try:
        doc = load_export(args.export)
    except Exception as e:  # noqa: BLE001
        print(f"crossval-est: cannot read export: {e}", file=sys.stderr)
        return 3
    # Fail loud on a kind this comparison cannot feed to pandapower (exit 3), before any work (and before the dependency check, so a bad input is never reported as a skip), naming it.
    used = doc["measurements"]["items"] if args.bad_data else [r["measurement"] for r in doc["residuals"]]
    bad = unsupported_kinds(used)
    if bad:
        print(f"crossval-est: unsupported measurement kind(s) {bad}; supported: {list(SUPPORTED_KINDS)}",
              file=sys.stderr)
        return 3

    # D-DOC-127: a missing NumPy or pandapower is a skip, exit 77, naming the package and the interpreter (it was
    # exit 2). Still after the input checks above, so a bad input is never reported as a skip.
    require("crossval-est", "numpy", "pandapower")  # import pandapower  # noqa: F401 (the dependency check)

    model = doc["model"]
    try:
        net = convert_net(model)
        ppbus = bus_index_map(net)
        branch_map = branch_element_map(model, net, ppbus)
        if args.bad_data:
            # The original set, gross errors and all.
            original = [m for m in doc["measurements"]["items"]]
            add_measurements(net, model, original, ppbus, branch_map)
            report = compare_bad_data(doc, net, model)
            ok = report["counts_agree"]
        else:
            # The set the estimate was finally computed from (post-removal), from the residual table.
            final_set = [r["measurement"] for r in doc["residuals"]]
            add_measurements(net, model, final_set, ppbus, branch_map)
            run_estimation(net)
            report = diff_states(doc, net, args.tol_vm, args.tol_va)
            ok = report["within_tolerance"]
    except Exception as e:  # noqa: BLE001
        print(f"crossval-est: pandapower estimation failed: {e}", file=sys.stderr)
        return 4

    text = json.dumps(report, indent=2)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(text)
    print(text)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
