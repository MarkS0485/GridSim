#!/usr/bin/env python3
"""Independent NumPy cross-validation of a GridSim impedance-based stability scan.

Where crossval_harmonics.py asks "does an independent solver land on the same Y(h) V = I?", this asks the
same of the impedance-stability screen: for the canonical single-inverter-vs-Thevenin case, does GridSim's
converter admittance Y_conv, its grid impedance Z_grid, the return ratio L = Z_grid * Y_conv, the encirclement
verdict, and the critical-SCR boundary agree with the SAME physics rebuilt from the exported parameters using
nothing but NumPy?

It is deliberately independent, not a wrapper: it does NOT call any GridSim code. It reads the
`gridsim-impedance-stability/1` export and re-derives, from the recorded `converterParams`:

  * SRF-PLL closed loop  G_pll(s) = (kp s + ki) / (s^2 + Vd0 kp s + Vd0 ki)      (or 1/Vd0 for an ideal PLL)
  * current-loop roll-off  G_ci(s) = wci / (s + wci)
  * positive-sequence converter admittance  Y+(jw) = 1/2 (G_ci/Vd0 - G_pll)(Id0 + j Iq0)
  * canonical Thevenin grid impedance in the converter's dq frame  Z_pp(f) = R + jX ((f + f0)/f0),
    |Z|=1/(SCR*P), X/R = xOverR (and Z_nn(f) = R + jX ((f - f0)/f0) for the negative-sequence loop)
  * return ratio  L = Z_pp * Y+, the winding number of (1 + Z_pp Y_pp)(1 + Z_nn Y_nn) about the origin, and the
    critical SCR (the sequence-domain SISO criterion; Y_nn = 1/2 (G_ci/Vd0 - G_pll)(Id0 - j Iq0))

and diffs them against the exported gridImpedance / converterAdmittance / minorLoop / nyquist / boundary
blocks. Exit 0 iff every comparison is within tolerance; 1 on any mismatch. (D-DOC-175: Y_conv is read from
converterAdmittance; an export written before that key existed carries it only under the old, misnamed key
converterImpedance, which is read instead. Both keys hold the same admittance values.)

Reference physics (impedance-based weak-grid stability): Sun, "Impedance-Based Stability Criterion for
Grid-Connected Inverters" (2011); Wen/Boroyevich/Burgos/Mattavelli/Shen, dq impedance of grid-tied inverters
(2015-2016); Rygg/Molinas, modified-sequence / dq equivalence (2016). The published weak-grid SCR boundaries
these establish are the physical reference points; this script validates GridSim's numerics against its own
recorded parameters on whatever scan it is handed.

Usage:
    python crossval_impedance.py --export <impedance-stability-export.json> [--tol R] [--atol A]

Default tolerances (N142, October 2026): 4096 machine epsilons, both relative and absolute (about 9.09e-13; the
absolute one in pu), the same count N005 chose for crossval_harmonics.py; a comparison passes when
|mine - GridSim| <= atol + rtol * |GridSim|. Re-measured on 2026-10-31 (GridSim 2.0.200 Release build, after the grid and
the converter were paired in the same frame and MIMO became the default criterion for a grid-following converter) on 25
flag-built grid-following scans (case9, ieee9, case14, case24, case30, case57, case118, uk, gb-reduced-29 at the defaults;
case9 and case118 with --current-bw 200, --outer-loop-only, --pll-bw 20 --pll-damping 0.4, --scr 1.2, --scr 10,
--criterion siso (was --criterion mimo, now the default), --fmin 0.01 --fmax 2000 --ppd 200, and --pll-bw 1
--current-bw 50 --scr 1.5; 18,165 Z / Y / L comparisons, every scan PASS at the defaults): the smallest k for which
rtol = atol = k eps passes everything is k = 28.06 (gb-reduced-29, Y at f = 0.7236 Hz: |d| 6.59e-15 pu on |Y| 0.0579 pu,
at an operating current where the PLL and current-loop terms nearly cancel); on every other scan k is at most 3.24
(case118 --outer-loop-only). The default is therefore about 146 times the worst measured need. (The 1.95.129
measurement, before the frame pairing, was k = 25.55 at 0.1096 Hz on the same case.)
The relative
error alone is not a usable measure: at low frequency Y+ = 1/2 (G_ci/Vd0 - G_pll) I0 is a near-cancellation (both
terms tend to 1/Vd0), so |Y| is small while the absolute agreement stays at a few eps (the worst pure relative
difference measured was 2.13e-11, Y at 0.011 Hz on case118 with --fmin 0.01, where the absolute difference is 7.03e-16
pu); the absolute term carries those points. The old
defaults (1e-6 relative, 1e-9 absolute) would have passed a 1e-7 regression. Pass --tol / --atol to loosen them
deliberately.

A `--grid real` export (converterParams.canonical false, D-DOC-045, October 2026): the grid impedance is the
network's own harmonic driving-point impedance at the converter bus, which this script does not rebuild, and the
Nyquist verdict and SCR boundary are for that impedance, not the canonical Thevenin. So only what can be re-derived is
compared: the converter admittance Y+ (from the recorded parameters, exactly as for a canonical scan) and the return
ratio L = Z_grid * Y+ formed from the EXPORTED Z_grid and this script's own Y+. The Z_grid, winding and boundary
comparisons are skipped and the report says so. Before, the script rebuilt the canonical Thevenin anyway and failed
every frequency (case9: 171 mismatches, |dZ| up to 369 pu, exit 1).

Exit codes: 0 = all within tol, 1 = mismatch, 3 = unreadable input, 77 = NOT CHECKED (no independent derivation ran:
the export has no converterParams, as for a grid-forming or asset-class run, and so nothing was compared; or numpy is
missing). R103: that case used to print PASS and exit 0. Numpy missing was exit 2 before D-DOC-127.
"""
import argparse
import json
import sys

# N142: the default tolerances, in machine epsilons (see the module docstring). 2**-52 is the IEEE-754 double machine
# epsilon (sys.float_info.epsilon).
EPS = 2.0 ** -52
DEFAULT_TOL_EPS = 4096
DEFAULT_RTOL = DEFAULT_TOL_EPS * EPS
DEFAULT_ATOL = DEFAULT_TOL_EPS * EPS

from sidecar_common import require

# D-DOC-127 (October 2026): a missing NumPy is a skip, exit 77, naming the interpreter (it was exit 2, which Python also
# uses for a usage error).
require("crossval-impedance", "numpy")
import numpy as np  # noqa: E402  (checked just above)

EXPORT_SCHEMA = "gridsim-impedance-stability/1"

# The conventional "skipped" exit code: nothing was validated. Distinct from 0 (agrees) and 1 (mismatch).
EXIT_NOT_CHECKED = 77


def _f(v, d=0.0):
    try:
        return float(v)
    except (TypeError, ValueError):
        return d


def load_export(path):
    with open(path, "r", encoding="utf-8") as fh:
        doc = json.load(fh)
    if doc.get("schema") != EXPORT_SCHEMA:
        raise ValueError(f"not a gridsim impedance-stability export (schema={doc.get('schema')!r})")
    return doc


def thevenin_rx(scr, xoverr, prated):
    zmag = 1.0 / (scr * prated)
    den = np.sqrt(1.0 + xoverr * xoverr)
    return zmag / den, zmag * xoverr / den


def g_pll(w, cp):
    if cp["idealPll"]:
        return complex(1.0 / cp["vd0"], 0.0)
    s = 1j * w
    kp, ki, vd0 = cp["pllKp"], cp["pllKi"], cp["vd0"]
    return (kp * s + ki) / (s * s + vd0 * kp * s + vd0 * ki)


def g_ci(w, cp):
    s = 1j * w
    wci = cp["currentLoopWciRadS"]
    return wci / (s + wci)


def y_pos(w, cp):
    """Positive-sequence converter admittance on the SYSTEM base: the converter-base admittance times pRatedPu.
    GridSim reports converterAdmittance (and forms L = Z_grid * Y) on the system base since 1.95.038 (R008); at
    pRatedPu = 1 the two bases coincide, so every earlier export compares exactly as before."""
    return 0.5 * (g_ci(w, cp) / cp["vd0"] - g_pll(w, cp)) * complex(cp["id0"], cp["iq0"]) * cp["pRatedPu"]


def y_neg(w, cp):
    """Negative-sequence converter admittance (system base): the modified-sequence Y_nn of the same dq admittance."""
    return 0.5 * (g_ci(w, cp) / cp["vd0"] - g_pll(w, cp)) * complex(cp["id0"], -cp["iq0"]) * cp["pRatedPu"]


def z_grid_pos(f, cp):
    """Z_pp(f): the canonical Thevenin in the converter's dq frame, the stationary impedance at f + f0."""
    return z_grid_pos_scr(f, cp, cp["scr"])


def z_grid_pos_scr(f, cp, scr):
    r, x = thevenin_rx(scr, cp["xOverR"], cp["pRatedPu"])
    return complex(r, x * ((f + cp["f0Hz"]) / cp["f0Hz"]))


def z_grid_neg_scr(f, cp, scr):
    """Z_nn(f): the stationary impedance at f - f0."""
    r, x = thevenin_rx(scr, cp["xOverR"], cp["pRatedPu"])
    return complex(r, x * ((f - cp["f0Hz"]) / cp["f0Hz"]))


def winding_siso(cp, scr, fmin=1e-3, fmax=1e5, npts=4000):
    """Net encirclements of the origin by (1 + Z_pp Y_pp)(1 + Z_nn Y_nn) over the wide contour. The product is
    conjugate-symmetric (L_pp(-jw) = conj(L_nn(jw))), so the conjugate mirror closes the contour."""
    fs = np.logspace(np.log10(fmin), np.log10(fmax), npts)
    w = 2 * np.pi * fs
    ret = np.array([(1.0 + z_grid_pos_scr(fi, cp, scr) * y_pos(wi, cp))
                    * (1.0 + z_grid_neg_scr(fi, cp, scr) * y_neg(wi, cp)) for fi, wi in zip(fs, w)])
    full = np.concatenate([np.conj(ret)[::-1], ret])
    ph = np.unwrap(np.angle(full))
    return int(round((ph[-1] - ph[0]) / (2 * np.pi)))


def close(a, b, rtol, atol):
    return abs(a - b) <= atol + rtol * abs(b)


def run(doc, rtol, atol):
    cp = doc.get("converterParams")
    if cp is None:
        print("crossval-impedance: export has no converterParams (non-GFL / real-network run); "
              "independent re-derivation is limited to the exported return ratio.")
    # Assemble a flat param dict for the derivations.
    op = doc["operatingPoint"]
    params = None
    if cp is not None:
        params = dict(
            pllKp=_f(cp["pllKp"]), pllKi=_f(cp["pllKi"]), currentLoopWciRadS=_f(cp["currentLoopWciRadS"]),
            idealPll=bool(cp["idealPll"]), scr=_f(cp["scr"]), xOverR=_f(cp["xOverR"]),
            pRatedPu=_f(cp["pRatedPu"]), f0Hz=_f(cp["f0Hz"]),
            vd0=_f(op["vd0Pu"]), id0=_f(op["id0Pu"]), iq0=_f(op["iq0Pu"]),
        )

    fails = 0
    checks = 0          # independent comparisons actually made; 0 means NOT CHECKED, never PASS (R103)
    max_dz = max_dy = max_dl = 0.0
    criterion = doc.get("criterion", "Siso")
    # D-DOC-045: a --grid real scan carries converterParams with canonical = false. Its Z_grid is the network's own
    # driving-point impedance, not the canonical Thevenin this script can rebuild, so Z_grid, the winding and the
    # boundary are not re-derived; Y+ and L (from the exported Z_grid) still are. An older export without the flag is
    # canonical (the only kind that carried converterParams before the real-grid option).
    canonical = cp is None or bool(cp.get("canonical", True))
    if not canonical:
        print("crossval-impedance: real-network grid impedance (--grid real): Z_grid, the encirclement count and the SCR "
              "boundary are the network's own and are not re-derived here; comparing Y_conv and L = Z_grid(exported) * Y_conv.")

    # 1) Per-frequency Z_grid, Y_conv, L (only meaningful for the canonical SISO export).
    gi = {_f(x["fHz"]): complex(_f(x["re"]), _f(x["im"])) for x in doc.get("gridImpedance", [])}
    # D-DOC-175: the converter ADMITTANCE Y_conv. Read from converterAdmittance; an older export has only the misnamed
    # converterImpedance key (the same admittance values), which is the fallback.
    y_rows = doc.get("converterAdmittance")
    if y_rows is None:
        y_rows = doc.get("converterImpedance", [])
    ci = {_f(x["fHz"]): complex(_f(x["re"]), _f(x["im"])) for x in y_rows}
    ml = {_f(x["fHz"]): complex(_f(x["lRe"]), _f(x["lIm"])) for x in doc.get("minorLoop", [])}

    rows = []
    if params is not None:
        for f in sorted(gi):
            w = 2 * np.pi * f
            zref, yref, lref = gi.get(f), ci.get(f), ml.get(f)
            ymine = y_pos(w, params)
            if canonical:
                zmine = z_grid_pos(f, params)
            else:
                # D-DOC-045: the real network's Z_grid is taken as given (not compared), so L checks the product.
                zmine = zref
                zref = None
            lmine = zmine * ymine
            dz = abs(zmine - zref) if zref is not None else 0.0
            dy = abs(ymine - yref) if yref is not None else 0.0
            dl = abs(lmine - lref) if lref is not None else 0.0
            max_dz, max_dy, max_dl = max(max_dz, dz), max(max_dy, dy), max(max_dl, dl)
            okz = zref is None or close(zmine, zref, rtol, atol)
            oky = yref is None or close(ymine, yref, rtol, atol)
            okl = lref is None or close(lmine, lref, rtol, atol)
            checks += sum(ref is not None for ref in (zref, yref, lref))
            if not (okz and oky and okl):
                fails += 1
                rows.append((f, dz, dy, dl))

    # 2) Independent encirclement winding vs the exported nyquist verdict (SISO canonical only).
    wind_ok = True
    wind_mine = None
    ny = doc.get("nyquist", {})
    if params is not None and criterion == "Siso" and canonical:
        wind_mine = winding_siso(params, params["scr"])
        ref_enc = int(ny.get("encirclements", 0))
        # sign convention may differ; compare magnitude (unstable-mode count) and the stable flag.
        stable_ref = bool(ny.get("stable", True))
        wind_ok = (abs(wind_mine) == int(ny.get("unstableModeCount", 0))) and ((abs(wind_mine) == 0) == stable_ref)
        checks += 1
        if not wind_ok:
            fails += 1

    # 3) Critical SCR boundary re-derivation (SISO canonical only).
    boundary_ok = True
    crit_mine = None
    b = doc.get("boundary", {})
    scrs = [_f(s["scr"]) for s in doc.get("scrSweep", [])]
    if params is not None and criterion == "Siso" and canonical and len(scrs) >= 2:
        prev_stable, prev_scr = True, None
        for scr in scrs:
            unstable = abs(winding_siso(params, scr)) > 0
            if crit_mine is None and prev_scr is not None and prev_stable is False and not unstable:
                crit_mine = 0.5 * (prev_scr + scr)
            prev_stable, prev_scr = (not unstable), scr
        found_ref = bool(b.get("found", False))
        crit_ref = _f(b.get("criticalScr"), float("nan"))
        if found_ref and crit_mine is not None:
            step = (scrs[-1] - scrs[0]) / (len(scrs) - 1)
            boundary_ok = abs(crit_mine - crit_ref) <= 1.5 * step
        elif found_ref != (crit_mine is not None):
            boundary_ok = False
        checks += 1
        if not boundary_ok:
            fails += 1

    print(f"impedance cross-validation: case '{doc.get('model', {}).get('name', '?')}', "
          f"criterion {criterion}, mode {doc.get('mode')}")
    print(f"  converterParams present: {params is not None}   grid impedance: {'canonical Thevenin' if canonical else 'real network (not re-derived)'}")
    if params is not None:
        print(f"  per-frequency: max|dZ|={max_dz:.2e}  max|dY|={max_dy:.2e}  max|dL|={max_dl:.2e}  "
              f"(rtol={rtol:g} atol={atol:g})")
        if rows:
            print("  frequency mismatches:")
            for f, dz, dy, dl in rows[:12]:
                print(f"    f={f:>10.4f} Hz  dZ={dz:.2e}  dY={dy:.2e}  dL={dl:.2e}")
        if criterion == "Siso" and not canonical:
            print("  winding / boundary: not re-derived (real-network grid impedance)")
        if criterion == "Siso" and canonical:
            print(f"  winding: numpy encirclements={wind_mine}  export unstable-modes={ny.get('unstableModeCount')}  "
                  f"export stable={ny.get('stable')}  -> {'OK' if wind_ok else 'MISMATCH'}")
            print(f"  boundary: numpy critical SCR={crit_mine}  export={b.get('criticalScr')}  found={b.get('found')}  "
                  f"-> {'OK' if boundary_ok else 'MISMATCH'}")
    print(f"  independent comparisons made: {checks}")
    if checks == 0:
        print("RESULT: NOT CHECKED - no independent derivation ran, so nothing was compared")
        return None
    print("RESULT:", "PASS - all within tolerance" if fails == 0 else f"FAIL - {fails} mismatch(es)")
    return fails == 0


def main():
    ap = argparse.ArgumentParser(description="Independent NumPy cross-validation of a GridSim impedance-stability scan.")
    ap.add_argument("--export", required=True, help="gridsim-impedance-stability/1 export JSON")
    ap.add_argument("--tol", type=float, default=DEFAULT_RTOL,
                    help=f"relative tolerance (default {DEFAULT_TOL_EPS} machine epsilons = {DEFAULT_RTOL:.3g})")
    ap.add_argument("--atol", type=float, default=DEFAULT_ATOL,
                    help=f"absolute tolerance in pu (default {DEFAULT_TOL_EPS} machine epsilons = {DEFAULT_ATOL:.3g})")
    args = ap.parse_args()

    try:
        doc = load_export(args.export)
    except Exception as e:  # noqa: BLE001
        print(f"crossval-impedance: cannot read input: {e}", file=sys.stderr)
        return 3

    try:
        ok = run(doc, args.tol, args.atol)
    except Exception as e:  # noqa: BLE001
        print(f"crossval-impedance: comparison failed: {e}", file=sys.stderr)
        return 3
    if ok is None:
        return EXIT_NOT_CHECKED
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
