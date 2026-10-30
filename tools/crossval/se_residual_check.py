#!/usr/bin/env python3
"""Independent validation of a GridSim state-estimate export (gridsim-estimate-export/1).

Where crossval_estimation.py asks "does pandapower's WLS land on the same state?",
this asks the more fundamental questions, with no GridSim math involved:

  1. HONESTY   - do the reported residuals equal z - h(x_hat), with h evaluated from an
                 independently built Ybus / pi-model (borrowed from residual_check.py)?
  2. OPTIMALITY - is x_hat actually the WLS minimum? The independent Gauss-Newton step
                 dx = (Ht W H)^-1 Ht W (z - h(x_hat)) is computed with a FINITE-DIFFERENCE
                 Jacobian of the independent h (no shared analytic algebra); at a true
                 optimum it is negligible.
  3. OBJECTIVE - does the reported J equal sum((r_i/sigma_i)^2) recomputed here?

--leverage additionally recomputes the residual-covariance diagonal (leverage K_ii,
omega_ii = sigma_i^2 (1 - K_ii)) and cross-checks the export's criticality flags -
the arbiter for "is that measurement really critical, or was that numerical
cancellation in the estimator's LU path?".

Measurement kinds: VmPu, VaDeg, PInjMw, QInjMvar, PFlowMw, QFlowMvar, IMagPu, IAngDeg. Any other kind is
refused with exit 3, naming it.

Usage: python se_residual_check.py <estimate-export.json | run-dir> [--tol 1e-6] [--leverage]
Exit: 0 all checks pass, 1 mismatch, 3 unreadable or an unsupported measurement kind, 77 NumPy or SciPy absent
(nothing validated). D-DOC-127 (October 2026): a missing SciPy used to crash with a traceback at the optimality
check (exit 1, read as a mismatch); both packages are now checked before any work, with a clear skip.
"""
import argparse
import json
import os
import sys

from sidecar_common import require

# D-DOC-127: NumPy for everything, SciPy for the sparse optimality step; a missing one is a skip (exit 77).
require(None, "numpy", "scipy", "scipy.sparse", "scipy.sparse.linalg")
import numpy as np  # noqa: E402  (checked just above)

from residual_check import build_ybus, _f  # noqa: E402

SCHEMA = "gridsim-estimate-export/1"


def load_doc(path):
    if os.path.isdir(path):
        path = os.path.join(path, "estimate-export.json")
    with open(path, encoding="utf-8") as f:
        doc = json.load(f)
    if doc.get("schema") != SCHEMA:
        print(f"error: schema '{doc.get('schema')}' is not '{SCHEMA}'", file=sys.stderr)
        sys.exit(3)
    return doc


def branch_stamps(model, id2i):
    """(from,to,name) -> (f, t, yff, yft, ytf, ytt) per branch, standard pi-model."""
    stamps = []
    for e in model["branches"]:
        f, t = id2i[e["from"]], id2i[e["to"]]
        z = complex(_f(e.get("r")), _f(e.get("x")))
        y = 1.0 / z if z != 0 else 0.0
        bc = 1j * _f(e.get("b")) / 2.0
        tap = _f(e.get("tap"))
        a = tap if tap != 0 else 1.0
        T = a * np.exp(1j * np.radians(_f(e.get("phaseShiftDeg"))))
        stamps.append((e, f, t,
                       (y + bc) / (a * a),      # yff
                       -y / np.conj(T),          # yft
                       -y / T,                   # ytf
                       y + bc))                  # ytt
    return stamps


def resolve_branch(stamps, m):
    """A measurement's (from,to[,circuit]) -> its stamp tuple, mirroring GridSim's resolution
    (including the '#k' ordinal-circuit form for unnamed parallel branches)."""
    circuit = m.get("circuit")
    if circuit and circuit.startswith("#"):
        s = stamps[int(circuit[1:])]
        if s[0]["from"] != m["from"] or s[0]["to"] != m["to"]:
            print(f"error: circuit {circuit} endpoints differ from {m['from']}->{m['to']}", file=sys.stderr)
            sys.exit(3)
        return s
    hits = [s for s in stamps
            if s[0]["from"] == m["from"] and s[0]["to"] == m["to"]
            and (circuit is None or (s[0].get("name") or "") == circuit)]
    if len(hits) != 1:
        print(f"error: measurement branch {m['from']}->{m['to']} resolved to {len(hits)} branches",
              file=sys.stderr)
        sys.exit(3)
    return hits[0]


# Every measurement kind this checker evaluates independently. R308: VaDeg (a PMU / phase-angle row, which
# `estimate --phaseangle` writes) and the branch current kinds IMagPu / IAngDeg used to fall into the branch-flow
# branch and die with a KeyError on a row with no from/to; anything not listed now fails loud with exit 3.
SUPPORTED_KINDS = ("VmPu", "VaDeg", "PInjMw", "QInjMvar", "PFlowMw", "QFlowMvar", "IMagPu", "IAngDeg")
ANGLE_KINDS = ("VaDeg", "IAngDeg")       # reported in degrees, held here in degrees
BRANCH_KINDS = ("PFlowMw", "QFlowMvar", "IMagPu", "IAngDeg")


def require_supported(measurements):
    """Fail loud (exit 3) on a measurement kind this checker cannot evaluate, naming it."""
    bad = sorted({m.get("kind") for m in measurements if m.get("kind") not in SUPPORTED_KINDS}, key=str)
    if bad:
        print(f"se-residual-check: unsupported measurement kind(s) {bad}; supported: {list(SUPPORTED_KINDS)}",
              file=sys.stderr)
        sys.exit(3)


def h_engineering(measurements, Y, stamps, id2i, base, vm, va, z=None):
    """Evaluate every measurement's h at (vm, va), in engineering units (pu / deg / MW / MVAr).

    Angle kinds (VaDeg, IAngDeg) are degrees. When the measured values z are given, an angle h is folded onto the
    branch of z (so z - h lies in (-180, 180]): a PMU reports the wrapped principal angle and atan2 can land on the
    other side of the cut, which would otherwise read as a ~360 degree residual. GridSim folds h the same way.
    The current kinds are textbook circuit quantities at the measured end of the pi model: I_end = y_aa V_a +
    y_ab V_b; IMagPu = |I_end| (pu), IAngDeg = arg(I_end) in degrees (equal to Va_end - atan2(Q, P))."""
    V = vm * np.exp(1j * va)
    S = V * np.conj(Y @ V) * base
    out = np.zeros(len(measurements))
    for r, m in enumerate(measurements):
        kind = m["kind"]
        if kind == "VmPu":
            out[r] = vm[id2i[m["bus"]]]
        elif kind == "VaDeg":
            out[r] = np.degrees(va[id2i[m["bus"]]])
        elif kind == "PInjMw":
            out[r] = S.real[id2i[m["bus"]]]
        elif kind == "QInjMvar":
            out[r] = S.imag[id2i[m["bus"]]]
        elif kind in BRANCH_KINDS:
            _, f, t, yff, yft, ytf, ytt = resolve_branch(stamps, m)
            if m.get("end") == "To":
                i_end = ytt * V[t] + ytf * V[f]
                v_end = V[t]
            else:
                i_end = yff * V[f] + yft * V[t]
                v_end = V[f]
            if kind == "IMagPu":
                out[r] = abs(i_end)
            elif kind == "IAngDeg":
                out[r] = np.degrees(np.angle(i_end))
            else:
                s_end = v_end * np.conj(i_end) * base
                out[r] = s_end.real if kind == "PFlowMw" else s_end.imag
        else:
            raise ValueError(f"unsupported measurement kind {kind!r}")   # require_supported() runs first
        if z is not None and kind in ANGLE_KINDS:
            out[r] = z[r] - np.remainder(z[r] - out[r] + 180.0, 360.0) + 180.0
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("export")
    ap.add_argument("--tol", type=float, default=1e-6,
                    help="residual-honesty tol (engineering units) and optimality-step tol (pu/rad)")
    ap.add_argument("--fd", action="store_true",
                    help="also cross-check the analytic H against central differences (small cases)")
    ap.add_argument("--leverage", action="store_true",
                    help="recompute leverage/criticality and diff against the export's flags")
    args = ap.parse_args()

    doc = load_doc(args.export)
    model = doc["model"]
    buses = model["buses"]
    id2i = {b["id"]: i for i, b in enumerate(buses)}
    n = len(buses)
    base = _f(model.get("baseMva"), 100.0)

    Y = build_ybus(buses, model["branches"], id2i, base)
    stamps = branch_stamps(model, id2i)

    est = doc["estimate"]
    vm = np.ones(n)
    va = np.zeros(n)
    for b in est["buses"]:
        i = id2i[b["id"]]
        vm[i] = _f(b["vm"])
        va[i] = np.radians(_f(b["vaDeg"]))

    measurements = [r["measurement"] for r in doc["residuals"]]
    require_supported(measurements)
    z = np.array([_f(m["value"]) for m in measurements])
    sigma = np.array([_f(m["sigma"]) for m in measurements])
    reported_res = np.array([_f(r["residual"]) for r in doc["residuals"]])

    failures = []

    # 1. HONESTY: reported residuals vs independent z - h(x_hat).
    h0 = h_engineering(measurements, Y, stamps, id2i, base, vm, va, z)
    dres = np.max(np.abs((z - h0) - reported_res))
    print(f"[se-check] residual honesty: max |(z - h_indep) - reported| = {dres:.3e}")
    if dres > args.tol:
        failures.append(f"residuals differ from independent h by {dres:.3e} (> {args.tol})")

    # 2. OBJECTIVE: recomputed J vs reported.
    J = float(np.sum(((z - h0) / sigma) ** 2))
    dJ = abs(J - _f(est["objectiveJ"]))
    print(f"[se-check] objective: recomputed J = {J:.6f}  reported = {_f(est['objectiveJ']):.6f}  |dJ| = {dJ:.3e}")
    if dJ > max(1e-6, 1e-9 * max(J, 1.0)):
        failures.append(f"objective J mismatch {dJ:.3e}")

    # 3. OPTIMALITY: the independent Gauss-Newton step from x_hat must be negligible.
    #    State = [Va at non-slack (rad); Vm at all buses (pu)], same convention as GridSim.
    #    H is built ANALYTICALLY here (this file's own textbook partials over its own Ybus -
    #    still fully independent of GridSim's code) and held sparse, so the check runs at
    #    gb-full scale; --fd cross-checks the analytic rows by central differences on
    #    small cases (the instrument validating itself).
    from scipy import sparse as sp
    from scipy.sparse.linalg import splu

    # Per-row scale from engineering units to the estimator's pu / radian units: Vm and |I| are already pu, angles
    # are degrees (scale 180/pi to radians), powers are MW / MVAr (scale baseMVA).
    scale = np.array([1.0 if m["kind"] in ("VmPu", "IMagPu")
                      else np.degrees(1.0) if m["kind"] in ANGLE_KINDS
                      else base for m in measurements])
    z_pu, sig_pu = z / scale, sigma / scale
    slack = [i for i, b in enumerate(buses) if str(b["type"]).lower() == "slack"]
    ang_col = {}
    for i in range(n):
        if i not in slack:
            ang_col[i] = len(ang_col)
    n_ang = len(ang_col)
    s_dim = n_ang + n

    def h_pu(vmx, vax):
        return h_engineering(measurements, Y, stamps, id2i, base, vmx, vax, z) / scale

    V = vm * np.exp(1j * va)
    S = V * np.conj(Y @ V)                      # pu injections at x_hat
    Gy, By = Y.real, Y.imag
    neigh = [np.nonzero((Gy[i] != 0) | (By[i] != 0))[0] for i in range(n)]

    rows_i, cols_i, vals = [], [], []

    def put(r, c, v):
        if v != 0.0:
            rows_i.append(r); cols_i.append(c); vals.append(v)

    for r, m in enumerate(measurements):
        kind = m["kind"]
        if kind == "VmPu":
            put(r, n_ang + id2i[m["bus"]], 1.0)
            continue
        if kind == "VaDeg":
            # d(Va_i)/d(Va_i) = 1 in radians; the slack angle is not a state, so its row is empty.
            i = id2i[m["bus"]]
            if i in ang_col:
                put(r, ang_col[i], 1.0)
            continue
        if kind in ("IMagPu", "IAngDeg"):
            # The current rows are built by central differences of this file's own h over the (at most four)
            # states they touch: still fully independent of GridSim's code. --fd cross-checks them like the rest.
            _, f, t, _, _, _, _ = resolve_branch(stamps, m)
            one = [m]
            zr = None if z is None else z[r:r + 1]
            sc = scale[r]
            eps = 1e-7
            for bus in (f, t):
                for col, vec in ((ang_col.get(bus), va), (n_ang + bus, vm)):
                    if col is None:
                        continue
                    vec[bus] += eps
                    hp = h_engineering(one, Y, stamps, id2i, base, vm, va, zr)[0]
                    vec[bus] -= 2 * eps
                    hmn = h_engineering(one, Y, stamps, id2i, base, vm, va, zr)[0]
                    vec[bus] += eps
                    put(r, col, (hp - hmn) / (2 * eps) / sc)
            continue
        if kind in ("PInjMw", "QInjMvar"):
            i = id2i[m["bus"]]
            real = kind == "PInjMw"
            Pi, Qi = S.real[i], S.imag[i]
            for k in neigh[i]:
                th = va[i] - va[k]
                cth, sth = np.cos(th), np.sin(th)
                gik, bik = Gy[i, k], By[i, k]
                if k == i:
                    dth = -Qi - By[i, i] * vm[i] ** 2 if real else Pi - Gy[i, i] * vm[i] ** 2
                    dvm = Pi / vm[i] + Gy[i, i] * vm[i] if real else Qi / vm[i] - By[i, i] * vm[i]
                else:
                    dth = vm[i] * vm[k] * ((gik * sth - bik * cth) if real else -(gik * cth + bik * sth))
                    dvm = vm[i] * ((gik * cth + bik * sth) if real else (gik * sth - bik * cth))
                if int(k) in ang_col:
                    put(r, ang_col[int(k)], dth)
                put(r, n_ang + int(k), dvm)
            continue
        # branch flow at the measured end
        _, f, t, yff, yft, ytf, ytt = resolve_branch(stamps, m)
        a_, b_ = (f, t) if m.get("end") != "To" else (t, f)
        yaa, yab = (yff, yft) if m.get("end") != "To" else (ytt, ytf)
        gaa, baa = yaa.real, yaa.imag
        gab, bab = yab.real, yab.imag
        vA, vB = vm[a_], vm[b_]
        th = va[a_] - va[b_]
        cth, sth = np.cos(th), np.sin(th)
        real = kind == "PFlowMw"
        dthA = vA * vB * ((-gab * sth + bab * cth) if real else (gab * cth + bab * sth))
        dvmA = (2 * gaa * vA + vB * (gab * cth + bab * sth)) if real else (-2 * baa * vA + vB * (gab * sth - bab * cth))
        dvmB = vA * ((gab * cth + bab * sth) if real else (gab * sth - bab * cth))
        if a_ in ang_col:
            put(r, ang_col[a_], dthA)
        if b_ in ang_col:
            put(r, ang_col[b_], -dthA)
        put(r, n_ang + a_, dvmA)
        put(r, n_ang + b_, dvmB)

    H = sp.csr_matrix((vals, (rows_i, cols_i)), shape=(len(measurements), s_dim))

    if getattr(args, "fd", False):
        eps = 1e-7
        Hd = H.toarray()
        maxerr = 0.0
        for c in range(s_dim):
            if c < n_ang:
                i = [k for k, v in ang_col.items() if v == c][0]
                va[i] += eps; hp = h_pu(vm, va); va[i] -= 2 * eps; hmn = h_pu(vm, va); va[i] += eps
            else:
                i = c - n_ang
                vm[i] += eps; hp = h_pu(vm, va); vm[i] -= 2 * eps; hmn = h_pu(vm, va); vm[i] += eps
            maxerr = max(maxerr, float(np.max(np.abs(Hd[:, c] - (hp - hmn) / (2 * eps)))))
        print(f"[se-check] --fd self-check: max |analytic - central-difference| = {maxerr:.3e}")
        if maxerr > 1e-6:
            failures.append(f"analytic H disagrees with finite differences by {maxerr:.3e}")

    W = 1.0 / sig_pu ** 2
    r_pu = z_pu - h_pu(vm, va)
    Wd = sp.diags(W)
    G = (H.T @ Wd @ H).tocsc()
    g = H.T @ (W * r_pu)
    lu = splu(G)
    step = lu.solve(g)
    step_inf = float(np.max(np.abs(step)))
    print(f"[se-check] optimality: independent gauss-newton step |dx|_inf = {step_inf:.3e}")
    if step_inf > args.tol:
        failures.append(f"x_hat is not stationary: independent step {step_inf:.3e} (> {args.tol})")

    # 4. Optional: leverage / criticality arbitration.
    if args.leverage:
        Hd = H.tocsr()
        K = np.empty(len(measurements))
        for r in range(len(measurements)):
            hrow = np.asarray(Hd.getrow(r).todense()).ravel()
            K[r] = float(hrow @ lu.solve(hrow)) * W[r]   # leverage in [0, 1]
        omega = sig_pu ** 2 * (1.0 - K)
        flagged = [(_r, resid) for _r, resid in enumerate(doc["residuals"]) if resid.get("critical")]
        print(f"[se-check] leverage: max K = {np.max(K):.9f}   export flags {len(flagged)} critical")
        for idx, resid in flagged:
            truly = K[idx] > 1.0 - 1e-6
            m = measurements[idx]
            where = f"bus {m.get('bus')}" if m.get("bus") is not None else f"{m.get('from')}->{m.get('to')}"
            print(f"    critical[{idx}] {m['kind']} @ {where}: independent K = {K[idx]:.9f} "
                  f"omega = {omega[idx]:.3e} -> {'CONFIRMED' if truly else 'NOT critical (numerical artifact)'}")
            if not truly:
                failures.append(f"measurement {idx} flagged critical but independent leverage K = {K[idx]:.6f}")
        near = int(np.sum(K > 1.0 - 1e-6))
        unflagged_critical = near - sum(1 for i, _ in flagged if K[i] > 1.0 - 1e-6)
        if unflagged_critical > 0:
            failures.append(f"{unflagged_critical} measurements have K ~ 1 but are not flagged critical")

    print()
    if failures:
        for f_ in failures:
            print(f"[se-check] FAIL: {f_}")
        sys.exit(1)
    print("[se-check] PASS: residuals honest, objective consistent, x_hat is the independent WLS optimum")
    sys.exit(0)


if __name__ == "__main__":
    main()
