#!/usr/bin/env python3
"""Cross-validate a GridSim frequency (swing) solve against an independent scipy integration.

Reads a GridSim frequency-export JSON (schema `gridsim-frequency-export/1`) and re-integrates the SAME swing
model with scipy's ODE solver, then compares the independent RoCoF/nadir against GridSim's. This checks that the
frequency-dynamics engine's explicit-Euler trace agrees with a higher-order integrator of the identical physics -
i.e. the numerics, not just the formula, hold up.

Usage:
    python crossval_freq.py <frequency-export.json> [--tol-rocof 0.02] [--tol-nadir 0.05] [--out diff.json]

Requires: scipy, numpy. Exit codes: 0 = within tol, 1 = mismatch, 3 = setup error (unreadable export or wrong
schema; N280, October 2026: it was 2, which Python also uses for a usage error, so a bad file read like a bad command
line; 3 is what crossval_classical.py, crossval_smallsignal.py and the other sidecars use), 77 = NOT CHECKED (the export
carries recorded response services or an offset pre-event start, which this re-integration does not model) or
SKIPPED (NumPy or SciPy is not installed in the interpreter that ran it; N245): in both cases nothing was validated.
"""
import argparse
import json
import sys

from sidecar_common import EXIT_SKIPPED, missing


def load_export(path):
    with open(path, "r", encoding="utf-8") as f:
        doc = json.load(f)
    if not doc.get("schema", "").startswith("gridsim-frequency-export/"):
        raise ValueError(f"not a gridsim frequency-export (schema={doc.get('schema')!r})")
    return doc


# The conventional "skipped" exit code: nothing was validated. Distinct from 0 (agrees) and 1 (mismatch).
EXIT_NOT_CHECKED = 77
assert EXIT_NOT_CHECKED == EXIT_SKIPPED   # one "nothing was validated" code across the sidecars (D-DOC-127)

# Frequency deviation (Hz) at which the generic fast frequency response is fully delivered: the linear FFR law
# is clamp(-(FFR / this) * df, -FFR, +FFR). Mirrors the C# model (FrequencyDynamics: 'full TSGB fast response
# at 0.5 Hz deviation'), which does not yet export it; if the export ever carries
# fastResponse.fullDeliveryDeviationHz, that value is used instead (R214; the export side is in the deferred
# frequency-dynamics area, see NOTES-FOR-MAIN.md).
FFR_FULL_DELIVERY_DEVIATION_HZ = 0.5


def unmodelled_physics(doc):
    """R056: the reasons this re-integration would NOT be the identical physics, or an empty list.

    The integrator below models governor droop, generic FFR and an optional forming block, starting at nominal
    frequency. A GridSim run that also delivered recorded response services (replay-event / scenario apply an
    event's recorded services) or started from an equilibrium offset (config.initialFrequencyHz) is different
    physics; comparing against it would report a MISMATCH, or a coincidental pass. crossval_h0.py skips such cells
    for the same reason."""
    reasons = []
    services = doc.get("systemResponseServices") or []
    if len(services) > 0:
        reasons.append(f"{len(services)} recorded response service(s)")
    f_init = doc.get("config", {}).get("initialFrequencyHz")
    if f_init is not None:
        reasons.append(f"offset pre-event start at {f_init} Hz")
    return reasons


def integrate(doc):
    import numpy as np
    from scipy.integrate import solve_ivp

    e = doc["inertia"]["effectiveInertiaMwS"]
    demand = doc["demandMw"]
    # Grid-forming (optional block): the denominator is the UNFLOORED E_real + Ev, so read it directly -
    # NOT effectiveInertiaMwS (which is Math.Max(1.0, ...)-floored and would mis-reconstruct low-E runs by
    # ~1 MW.s). Dv adds to the total damping; the setpoint Pset is a constant forcing.
    forming = doc.get("forming")
    forming_dv = 0.0
    forming_setpoint = 0.0
    if forming:
        e = forming["effectiveFormingInertiaMwS"]
        forming_dv = forming.get("formingDampingMwPerHz", 0.0)
        forming_setpoint = forming.get("formingSetpointMw", 0.0)
    ffr = doc["fastResponse"]["fastResponseMw"]
    ffr_tau = doc["fastResponse"]["timeConstantS"]
    ffr_db = doc["fastResponse"]["deadbandHz"]
    ffr_full = doc["fastResponse"].get("fullDeliveryDeviationHz", FFR_FULL_DELIVERY_DEVIATION_HZ)
    cfg = doc["config"]
    f0 = cfg["nominalHz"]
    droop = cfg["droopPu"]
    damp = cfg["dampingPctPerHz"] / 100.0 * demand + forming_dv   # + virtual former damping Dv (0 when no forming)
    presp = cfg["responsiveFraction"] * demand
    gov_tau = cfg["governorTimeConstantS"]
    duration = cfg["durationSeconds"]
    events = sorted(doc["events"], key=lambda x: x["atSeconds"])

    def p_event(t):
        return sum(ev["deltaPMw"] for ev in events if ev["atSeconds"] <= t)

    # State: [f, pGov, pFfr].
    def rhs(t, y):
        f, pgov, pffr = y
        df = f - f0
        pgov_target = -(df / f0) / droop * presp
        ffr_target = 0.0
        if ffr > 0 and abs(df) > ffr_db:
            ffr_target = max(-ffr, min(ffr, -(ffr / ffr_full) * df))
        p_imb = p_event(t) + pgov + pffr + forming_setpoint - damp * df
        return [f0 / (2.0 * e) * p_imb,
                (pgov_target - pgov) / gov_tau,
                (ffr_target - pffr) / max(1e-6, ffr_tau)]

    ts = np.linspace(0, duration, int(duration / 0.02) + 1)
    sol = solve_ivp(rhs, (0, duration), [f0, 0.0, 0.0], t_eval=ts, method="RK45",
                    rtol=1e-8, atol=1e-8, max_step=0.02)
    fseries = sol.y[0]
    nadir = float(fseries.min())
    # Initial RoCoF: the largest |df/dt| right after the first event.
    dfdt = np.gradient(fseries, ts)
    peak_rocof = float(dfdt[np.argmax(np.abs(dfdt))])
    return peak_rocof, nadir


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("export")
    ap.add_argument("--tol-rocof", type=float, default=0.02)
    ap.add_argument("--tol-nadir", type=float, default=0.05)
    ap.add_argument("--out")
    args = ap.parse_args()

    try:
        doc = load_export(args.export)
    except Exception as e:  # noqa: BLE001
        print(f"crossval-freq: setup error: cannot read export: {e}", file=sys.stderr)
        return 3   # N280: was 2 (Python's usage-error code); 3 = unreadable export, as the other sidecars
    reasons = unmodelled_physics(doc)
    if reasons:
        # Fail loud, not silently: say exactly why nothing was compared, and exit with the distinct skip code.
        report = {
            "schema": "gridsim-crossval-freq/1",
            "tool": "scipy.integrate.solve_ivp",
            "checked": False,
            "not_checked_because": reasons,
            "within_tolerance": None,
        }
        text = json.dumps(report, indent=2)
        if args.out:
            with open(args.out, "w", encoding="utf-8") as f:
                f.write(text)
        print(text)
        print("crossval-freq: NOT CHECKED - the export uses physics this re-integration does not model: "
              + "; ".join(reasons), file=sys.stderr)
        return EXIT_NOT_CHECKED
    # N245 (October 2026, the D-DOC-127 rule): a missing NumPy or SciPy is an ENVIRONMENT skip, exit 77 with the package
    # and the interpreter named (sidecar_common.missing), never exit 2 (which Python also uses for a usage error). The
    # export was read and the unmodelled-physics guard ran first, so a bad input is never reported as a skip.
    if missing("crossval_freq", "numpy", "scipy") is None:
        return EXIT_SKIPPED

    peak_rocof, nadir = integrate(doc)
    gs = doc["headline"]
    d_rocof = abs(abs(peak_rocof) - abs(gs["initialRocofHzPerS"]))
    d_nadir = abs(nadir - gs["nadirHz"])
    ok = d_rocof <= args.tol_rocof and d_nadir <= args.tol_nadir

    report = {
        "schema": "gridsim-crossval-freq/1",
        "tool": "scipy.integrate.solve_ivp",
        "checked": True,
        "rocof_gridsim": gs["initialRocofHzPerS"], "rocof_scipy": peak_rocof, "d_rocof": d_rocof,
        "nadir_gridsim": gs["nadirHz"], "nadir_scipy": nadir, "d_nadir": d_nadir,
        "within_tolerance": bool(ok),
    }
    text = json.dumps(report, indent=2)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(text)
    print(text)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
