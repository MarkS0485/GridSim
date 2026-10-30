#!/usr/bin/env python3
"""Cross-validate GridSim's classical multi-machine swing against an independent scipy integration.

Reads a `gridsim-classical-export/1` JSON (from `gridsim stability <case> --export <path>`) - the reduced
system (H, Pm, |E'|, delta0, the Kron-reduced admittance G+jB), a perturbed initial condition, and GridSim's
own RK4 rotor-angle trajectory sampled every 50 ms. It re-integrates the IDENTICAL classical swing with
scipy's RK45 and checks the two trajectories agree to a tight tolerance. This validates the multi-machine
coupling numerics (Pe = sum E'i E'j (Gij cos + Bij sin)) - the part the SMIB/2-machine analytic tests don't
fully exercise.

Usage:
    python crossval_classical.py <classical-export.json> [--tol 1e-3] [--out diff.json]

Requires: scipy, numpy. Exit codes: 0 = within tol, 1 = mismatch, 3 = setup error (unreadable export or wrong
schema; N248, October 2026: it was 2, which Python also uses for a usage error, so a bad file read like a bad command
line; 3 is what crossval_estimation.py and the other sidecars use), 77 = SKIPPED (NumPy or SciPy is not installed in
the interpreter that ran it, N245: nothing was validated).

Why the default --tol is still 1e-3 rad (N248, October 2026, recorded rather than changed): unlike the power-flow,
impedance, small-signal and harmonic sidecars (N243, N142: 4096 machine epsilons), this one does NOT compare like with
like. GridSim integrates with a FIXED-step RK4 (the export's stepSeconds); SciPy integrates with an ADAPTIVE RK45 at
rtol = atol = 1e-9. The difference is GridSim's RK4 truncation error over the run (order dt^4 per unit time, growing
with the swing), not a rounding difference, so no machine-epsilon default is meaningful here, and a tighter number
picked without measuring the bundled cases would turn real passes into false FAILs. The way to machine-epsilon level
is a like-for-like mode (re-integrate with the same fixed-step RK4 and the same dt, with the per-sample difference
then bounded by rounding of sin / cos and summation order across the steps) with an allowance measured on every case
the cross-check covers, as N243 did for gb-network-2224. That needs runs of `stability --export` and of this script,
so it is left to the next sidecar measurement batch (N248 in the LF1 notes). An explicit --tol still overrides.
"""
import argparse
import json
import sys

from sidecar_common import EXIT_SKIPPED, missing


def integrate(doc):
    import numpy as np
    from scipy.integrate import solve_ivp

    f0 = doc["f0"]
    ws = 2.0 * np.pi * f0
    machines = doc["machines"]
    n = len(machines)
    h = np.array([m["h"] for m in machines])
    pm = np.array([m["pm"] for m in machines])
    eq = np.array([m["eq"] for m in machines])
    g = np.array(doc["yReducedG"])
    b = np.array(doc["yReducedB"])
    d0 = np.array(doc["delta0Perturbed"])

    def pe(delta):
        # Pe_i = eq_i^2 G_ii + sum_{j!=i} eq_i eq_j (G_ij cos(deltai-deltaj) + B_ij sin(deltai-deltaj))
        out = eq * eq * np.diag(g)
        for i in range(n):
            for j in range(n):
                if j == i:
                    continue
                dij = delta[i] - delta[j]
                out[i] += eq[i] * eq[j] * (g[i, j] * np.cos(dij) + b[i, j] * np.sin(dij))
        return out

    def rhs(t, y):
        delta = y[:n]
        omega = y[n:]                      # per-unit speed deviation
        ddelta = ws * omega
        domega = (pm - pe(delta)) / (2.0 * h)
        return np.concatenate([ddelta, omega * 0 + domega])

    ts = np.array(doc["sampleTimes"])
    y0 = np.concatenate([d0, np.zeros(n)])
    sol = solve_ivp(rhs, (ts[0], ts[-1]), y0, t_eval=ts, method="RK45",
                    rtol=1e-9, atol=1e-9, max_step=0.01)
    return sol.y[:n].T                      # shape (samples, machines) - delta(t)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("export")
    ap.add_argument("--tol", type=float, default=1e-3)
    ap.add_argument("--out")
    args = ap.parse_args()

    try:
        with open(args.export, "r", encoding="utf-8") as f:
            doc = json.load(f)
        if not doc.get("schema", "").startswith("gridsim-classical-export/"):
            raise ValueError(f"not a classical export (schema={doc.get('schema')!r})")
    except Exception as e:  # noqa: BLE001
        print(f"crossval-classical: setup error: {e}", file=sys.stderr)
        return 3   # N248: was 2 (Python's usage-error code); 3 = unreadable export, as the other sidecars
    # N245 (October 2026, the D-DOC-127 rule): a missing NumPy or SciPy is an ENVIRONMENT skip, exit 77 with the package
    # and the interpreter named (sidecar_common.missing). It used to be folded into the setup error above (exit 2, which
    # Python also uses for a usage error). The export is read first, so a bad input is never reported as a skip.
    loaded = missing("crossval_classical", "numpy", "scipy")
    if loaded is None:
        return EXIT_SKIPPED
    np = loaded[0]

    scipy_delta = integrate(doc)
    gs_delta = np.array(doc["sampleDelta"])          # GridSim's RK4 trajectory
    max_diff = float(np.max(np.abs(scipy_delta - gs_delta)))
    ok = max_diff <= args.tol

    report = {
        "schema": "gridsim-crossval-classical/1",
        "tool": "scipy.integrate.solve_ivp (RK45)",
        "case": doc.get("label"),
        "machines": len(doc["machines"]),
        "samples": len(doc["sampleTimes"]),
        "max_abs_angle_diff_rad": max_diff,
        "tolerance_rad": args.tol,
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
