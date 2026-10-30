#!/usr/bin/env python3
"""Cross-validate a GridSim H=0 counterfactual study export (schema `gridsim-h0-study/1`).

Two independent re-derivations, from the export's OWN inputs (inertiaTable + axes + swing block), never
from GridSim's computed outputs:

  1. ARITHMETIC (every cell, machine-epsilon, no scipy):
       * E_real(scale) = scale * sum(H*MBase over synchronous rows)         -> == systemInertiaGvaS*1000
       * Ev separation: effectiveFormingInertiaMwS == E_real + virtualInertiaMwS, and E_real EXCLUDES Ev
       * bare RoCoF   = f0 * (-rocofSettingLossMw) / (2*E_real)              -> == bareInertiaRocofHzPerS
                        (R287, B4 October 2026: the loss in the first rocofSettingWindowS seconds, not the
                        whole staged total "largestLossMw"; an export written before R287 has no
                        rocofSettingLossMw and is checked against largestLossMw as it was written)
                        (and NULL when E_real == 0 - the un-formable sentinel)
       * forming-dependent iff (E_real == 0 AND Ev == 0); such cells carry null RoCoF/nadir + cascade

  2. FORMING ODE (representative formable cells, scipy RK45): independently re-integrate the fleet-FREE
     forming swing (governor + load damping + virtual former Ev/Dv) and diff peak RoCoF + nadir against
     the exported bareFormedRocofHzPerS / bareFormedNadirHz. This validates the NEW forming physics
     (Workstream A) with a higher-order integrator, not just closed-form arithmetic. Cells with recorded
     services or an equilibrium (offset) start are checked arithmetically only (their delivery-curve
     re-implementation is out of scope for this sidecar) and noted.

The fleet-COUPLED numbers (rocofHzPerS/nadirHz, trip cascade) are validated decisively by the in-suite
C# tests (H0StudyEngineTests); this sidecar is the independent numeric check on the transforms + forming
ODE. Requires numpy/scipy only for step 2. Exit codes: 0 = all runnable checks pass, 1 = mismatch,
3 = export unreadable (GR-906: was 2, which Python also uses for a usage error).
"""
import argparse
import json
import sys

TOL_E_REL = 1e-9
TOL_ROCOF = 0.02      # Hz/s, forming ODE vs export
TOL_NADIR = 0.05      # Hz


def load_export(path):
    with open(path, "r", encoding="utf-8") as f:
        doc = json.load(f)
    if not str(doc.get("schema", "")).startswith("gridsim-h0-study/"):
        raise ValueError(f"not a gridsim h0-study export (schema={doc.get('schema')!r})")
    return doc


def base_inertia_mws(doc):
    """sum(H*MBase) over the untransformed fleet (converters carry H=0 => contribute 0)."""
    return sum(r["hMwsPerMva"] * r["mBaseMva"] for r in doc["inertiaTable"])


def arithmetic_checks(doc):
    fails = []
    f0 = doc["system"]["nominalHz"]
    system = doc["system"]
    # R287: the analytic dP is the RoCoF-setting loss (first rocofSettingWindowS seconds). Older exports carry only
    # largestLossMw (the whole staged total, which is what they used).
    loss = system.get("rocofSettingLossMw", system["largestLossMw"])
    if "rocofSettingLossMw" in system:
        # Independent check from the export's own swing inputs: the deficit steps inside the window. Recorded operator
        # actions (positive steps) are not losses; they start well after the window in every bundled event.
        window = system["rocofSettingWindowS"]
        in_window = -sum(e["deltaPMw"] for e in doc["swing"]["events"]
                         if e["atSeconds"] <= window and e["deltaPMw"] < 0.0)
        if in_window > 0.0 and abs(in_window - loss) > 1e-9 * max(1.0, loss):
            fails.append(f"rocofSettingLossMw: export {loss} vs deficit steps in the first {window} s {in_window}")
        if loss > system["totalExogenousLossMw"] + 1e-9:
            fails.append(f"rocofSettingLossMw {loss} exceeds totalExogenousLossMw {system['totalExogenousLossMw']}")
    e_base = base_inertia_mws(doc)
    for i, c in enumerate(doc["cells"]):
        scale = c["syncInertiaScale"]
        ev = c["virtualInertiaMwS"]
        e_real = scale * e_base
        tol = TOL_E_REL * max(1.0, e_real)

        # E reconstruction (proves ScaleSynchronousInertia + SystemInertiaGvaS).
        if abs(e_real - c["systemInertiaGvaS"] * 1000.0) > tol:
            fails.append(f"cell[{i}] E_real: py {e_real} vs export {c['systemInertiaGvaS']*1000.0}")
        # Ev never enters real E; effective forming denominator = E_real + Ev, no floor.
        if abs(c["effectiveFormingInertiaMwS"] - (e_real + ev)) > max(tol, 1e-6):
            fails.append(f"cell[{i}] effForming: export {c['effectiveFormingInertiaMwS']} vs E_real+Ev {e_real+ev}")

        # bare inertia RoCoF + the un-formable sentinel.
        if e_real > 0.0:
            bare = f0 * (-loss) / (2.0 * e_real)
            got = c["bareInertiaRocofHzPerS"]
            if got is None or abs(bare - got) > 1e-9:
                fails.append(f"cell[{i}] bareRoCoF: py {bare} vs export {got}")
        else:
            if c["bareInertiaRocofHzPerS"] is not None:
                fails.append(f"cell[{i}] bareRoCoF sentinel: expected null at E_real=0, got {c['bareInertiaRocofHzPerS']}")

        # forming-dependent iff nothing forms the frequency (no real inertia AND no virtual former).
        expect_fd = (e_real == 0.0 and ev == 0.0)
        if bool(c["formingDependent"]) != expect_fd:
            fails.append(f"cell[{i}] formingDependent: export {c['formingDependent']} vs expected {expect_fd}")
        if c["formingDependent"]:
            for k in ("rocofHzPerS", "nadirHz", "bareFormedRocofHzPerS", "bareFormedNadirHz"):
                if c[k] is not None:
                    fails.append(f"cell[{i}] {k} must be null on a forming-dependent cell, got {c[k]}")
            if not c["cascadeLossOfMains"]:
                fails.append(f"cell[{i}] forming-dependent cell must flag cascadeLossOfMains")
    return fails


def responsive_fraction(base_fraction, cf, corner):
    """The responsive fraction a corner runs with: the base fraction times the corner's factor, capped by the corner's
    own `responsiveCap` when the export gives one (N283, LF3 October 2026).

    The export (R178) publishes each corner's cap beside its factors: 1.0 on the best corner, null (no cap) on the
    nominal and worst corners. This sidecar used to apply min(1.0, ...) on EVERY corner, so a study whose base
    responsive fraction was above 1 was capped here at the nominal corner while GridSim ran it uncapped, and the two
    diverged for a reason that was not a defect. Now a cap is applied only where the export says one applies.

    An export written before R178 has no `responsiveCap` key; for it the rule the export documents is used (cap 1.0 on
    the best corner only), so an old export is read the way it was produced.
    """
    scaled = base_fraction * cf["responsive"]
    if "responsiveCap" in cf:
        cap = cf["responsiveCap"]
    else:
        cap = 1.0 if corner == "best" else None
    if cap is None:
        return scaled
    return min(cap, scaled)


def integrate_forming(doc, cell):
    """RK45 re-integration of the fleet-free forming swing for one cell -> (peak_rocof, nadir)."""
    import numpy as np
    from scipy.integrate import solve_ivp

    sw = doc["swing"]
    cfg = sw["config"]
    f0 = cfg["nominalHz"]
    droop = cfg["droopPu"]
    demand = sw["demandMw"]
    e_base = base_inertia_mws(doc)
    e = cell["syncInertiaScale"] * e_base + cell["virtualInertiaMwS"]   # E_real + Ev (no floor)
    dv = cell["formingDampingMwPerHz"]

    corner = cell["corner"].lower()
    cf = sw["cornerFactors"][corner]
    damp = cfg["dampingPctPerHz"] / 100.0 * demand * cf["damping"] + dv
    presp = responsive_fraction(cfg["responsiveFraction"], cf, corner) * demand
    gov_tau = cfg["governorTimeConstantS"] * cf["governor"]
    duration = cfg["durationSeconds"]
    events = sorted(sw["events"], key=lambda x: x["atSeconds"])

    def p_event(t):
        return sum(e["deltaPMw"] for e in events if e["atSeconds"] <= t)

    def rhs(t, y):
        f, pgov = y
        df = f - f0
        pgov_target = -(df / f0) / droop * presp
        p_forcing = p_event(t) + pgov
        return [f0 / (2.0 * e) * (p_forcing - damp * df),
                (pgov_target - pgov) / gov_tau]

    ts = np.linspace(0, duration, int(duration / 0.02) + 1)
    sol = solve_ivp(rhs, (0, duration), [f0, 0.0], t_eval=ts, method="RK45",
                    rtol=1e-8, atol=1e-8, max_step=0.02)
    fseries, pgovseries = sol.y[0], sol.y[1]
    nadir = float(fseries.min())
    # Peak RoCoF on the SAME basis GridSim reports (the start-of-step analytic slope f0/(2E)*(pForcing-D*df),
    # sampled on the 0.02 s grid), not a centred finite difference - the two disagree at a staged-loss STEP,
    # where a centred difference straddles the discontinuity and underreads the true instantaneous slope.
    peak_rocof = 0.0
    for t, f, pgov in zip(ts, fseries, pgovseries):
        df = f - f0
        rocof = f0 / (2.0 * e) * (p_event(t) + pgov - damp * df)
        if abs(rocof) > abs(peak_rocof):
            peak_rocof = rocof
    return float(peak_rocof), nadir


def ode_checks(doc):
    """Returns (fails, checked_count, skipped_count). Only formable, no-service, nominal-start cells."""
    fails, checked, skipped = [], 0, 0
    has_services = len(doc["swing"].get("services", [])) > 0
    offset_start = doc["swing"]["config"].get("initialFrequencyHz") is not None
    for i, c in enumerate(doc["cells"]):
        if c["formingDependent"] or c["bareFormedRocofHzPerS"] is None:
            continue
        if has_services or offset_start:
            skipped += 1
            continue
        peak, nadir = integrate_forming(doc, c)
        d_rocof = abs(abs(peak) - abs(c["bareFormedRocofHzPerS"]))
        d_nadir = abs(nadir - c["bareFormedNadirHz"])
        checked += 1
        if d_rocof > TOL_ROCOF or d_nadir > TOL_NADIR:
            fails.append(f"cell[{i}] forming ODE: rocof py {peak:.4f} vs {c['bareFormedRocofHzPerS']:.4f} "
                         f"(d {d_rocof:.4f}); nadir py {nadir:.4f} vs {c['bareFormedNadirHz']:.4f} (d {d_nadir:.4f})")
    return fails, checked, skipped


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("export")
    ap.add_argument("--out")
    args = ap.parse_args()

    try:
        doc = load_export(args.export)
    except Exception as e:  # noqa: BLE001
        print(f"crossval-h0: cannot read export: {e}", file=sys.stderr)
        return 3   # GR-906: an unreadable input is 3 in every sidecar (2 is Python's own usage-error code)

    arith_fails = arithmetic_checks(doc)

    ode_fails, checked, skipped, scipy_ok = [], 0, 0, True
    try:
        import scipy  # noqa: F401
    except ImportError:
        scipy_ok = False

    if scipy_ok:
        ode_fails, checked, skipped = ode_checks(doc)

    ok = not arith_fails and not ode_fails
    report = {
        "schema": "gridsim-crossval-h0/1",
        "cells": len(doc["cells"]),
        "arithmetic_checks_failed": arith_fails,
        "forming_ode_cells_checked": checked,
        "forming_ode_cells_skipped": skipped,
        "forming_ode_failed": ode_fails,
        "scipy_available": scipy_ok,
        "within_tolerance": bool(ok),
        "note": ("arithmetic (E-reconstruction, Ev-separation, bare-RoCoF + sentinels) is machine-epsilon "
                 "and decisive for every cell; the forming ODE independently re-integrates the fleet-free "
                 "forming swing. Fleet-coupled cascade dynamics are validated by the C# H0StudyEngineTests."),
    }
    text = json.dumps(report, indent=2)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(text)
    print(text)
    if not scipy_ok:
        print("crossval-h0: scipy not installed - arithmetic checks ran; forming-ODE re-integration skipped.",
              file=sys.stderr)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
