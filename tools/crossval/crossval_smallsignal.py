#!/usr/bin/env python3
"""Cross-validate GridSim's small-signal / modal analysis against SciPy's dense non-symmetric eig.

Reads a `gridsim-smallsignal/1` JSON (from `gridsim modal <case> --export <path>`) - the real state matrix
A, GridSim's own eigenvalues, and its participation factors - then recomputes the eigenvalues (and the
left/right eigenvectors) with `scipy.linalg.eig(A)` and diffs.

IMPORTANT - this is NOT a machine-epsilon check. A NON-symmetric eigensolve (GridSim's Francis double-shift
QR vs LAPACK's) agrees only to ~cond(V)*eps in a RELATIVE sense, where V is the right-eigenvector matrix and
cond(V) blows up for near-defective / clustered modes. So the eigenvalue check is a PER-MODE RELATIVE
tolerance, scaled up by a conditioning guard cond(V). We report cond(V) explicitly so the reader can see
whether an "agreement" is machine-eps-clean (cond(V) ~ 1) or conditioning-limited.

Usage:
    python crossval_smallsignal.py <smallsignal-export.json> [--rtol R] [--out diff.json]

Default tolerance (N142, October 2026): rtol = 4096 machine epsilons (about 9.09e-13; was 1e-6), the same count N005
chose for crossval_harmonics.py. Measured with the 1.95.129 Release build against SciPy 1.18.1 on every bundled case
`gridsim modal` exports (case9 / ieee9, gb / gb-reduced / uk, gb-synthetic / synthetic, data/intl/ireland-all-island,
each undamped and with --damping 2): the smallest rtol that passes, max_rel / max(1, cond(V)), is at most
0.2165 machine epsilons (gb with --damping 2: max_rel 6.262e-14, cond(V) 1.303e3); the undamped near-defective cases
need at most 0.0874 eps (case9: max_rel 3.121e-8, cond(V) 1.928e10). The default is therefore about 18,900 times the
worst measured need. The margin is kept that wide on purpose: the gate scales by cond(V) but not by ||A||
(314.16 = 2 pi 50 on every classical case), so a well-conditioned matrix can legitimately differ by ~eps * ||A||
(about 315 eps) between two QR implementations, and cond(V) itself varies between LAPACK builds. The old 1e-6
(4.5e9 eps) would have passed an eigenvalue moved by a millionth of cond(V). Pass --rtol to loosen it deliberately.
One more export was checked and is NOT counted in the worst case above: gb-synthetic with --enable-inverters (116
states; `gridsim modal` itself exits 1 on it, the state matrix being defective). It passes the default only because
cond(V) is 5.52e13: its max_rel of 5.0 was a pairing artefact of the old (Im, Re) ordering, which sorted real
eigenvalues by imaginary parts that are rounding noise (+-7e-16 against +-5e-15) and so compared -20 with -3.33;
paired by nearest value the worst difference is 6.19e-8 relative.

Pairing (N178, October 2026): GridSim's eigenvalues and SciPy's are now paired by an OPTIMAL ASSIGNMENT
(scipy.optimize.linear_sum_assignment, the Hungarian method) on the same per-mode relative distance the gate uses,
|gs_i - sp_j| / max(|sp_j|, 1): the one-to-one pairing with the smallest total distance. Sorting both lists on
(Im, Re) and pairing by position breaks whenever two eigenvalues' imaginary parts are rounding noise of opposite sign
(the noise decides the order, not the value). The assignment cannot pair -20 with -3.33 when a -20 is available, so
an artefact like the 5.0 above no longer reaches the gate. Tolerances are unchanged (the fix makes the reported
difference honest; it does not loosen anything). The participation cross-check uses the same pairing. The report
states the pairing method and the worst-paired eigenvalues.

Requires: scipy, numpy. Exit codes: 0 = within tol, 1 = mismatch, 3 = setup error (unreadable export, wrong schema,
or an eigenvalue count that does not pair; N248, October 2026: it was 2, which Python also uses for a usage error),
77 = SKIPPED (NumPy or SciPy
is not installed in the interpreter that ran it, N245: nothing was validated).
"""
import argparse
import json
import sys

from sidecar_common import EXIT_SKIPPED, missing

# N142: the default per-mode relative tolerance, in machine epsilons (see the module docstring). 2**-52 is the IEEE-754
# double machine epsilon (sys.float_info.epsilon).
EPS = 2.0 ** -52
DEFAULT_RTOL_EPS = 4096
DEFAULT_RTOL = DEFAULT_RTOL_EPS * EPS


PAIRING_METHOD = ("optimal assignment (scipy.optimize.linear_sum_assignment) on |gs_i - sp_j| / max(|sp_j|, 1), "
                  "the per-mode relative distance the gate uses")


def pair_eigenvalues(gs, sp):
    """N178: pair GridSim's eigenvalues `gs` with SciPy's `sp` one to one.

    Returns `cols`, an integer array with gs[i] paired to sp[cols[i]], chosen to minimise the total relative distance
    sum_i |gs_i - sp_cols[i]| / max(|sp_cols[i]|, 1) (the Hungarian method). Fails loud (ValueError) when the two
    lists differ in length: an export whose eigenvalue count is not the state count cannot be cross-checked.

    Why not sort and zip: the old (Im, Re) lexsort ordered real eigenvalues by imaginary parts that are only rounding
    noise (+-1e-15), so the order of two real eigenvalues could differ between the two solvers and a position-wise
    zip compared unrelated values (gb-synthetic --enable-inverters: -20 against -3.33, max_rel 5.0, where the correct
    pairing gives 6.19e-8). An optimal assignment depends only on the values.
    """
    import numpy as np
    from scipy.optimize import linear_sum_assignment
    gs = np.asarray(gs, dtype=complex)
    sp = np.asarray(sp, dtype=complex)
    if gs.shape != sp.shape:
        raise ValueError(f"GridSim exported {gs.size} eigenvalues but SciPy found {sp.size}")
    if gs.size == 0:
        return np.zeros(0, dtype=int)
    cost = np.abs(gs[:, None] - sp[None, :]) / np.maximum(np.abs(sp), 1.0)[None, :]
    rows, cols = linear_sum_assignment(cost)
    # linear_sum_assignment returns rows 0..n-1 in order for a square matrix; check rather than assume.
    if not np.array_equal(rows, np.arange(gs.size)):
        raise ValueError("assignment did not return one pair per GridSim eigenvalue")
    return cols


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("export")
    ap.add_argument("--rtol", type=float, default=DEFAULT_RTOL,
                    help=f"per-mode relative tolerance before the cond(V) guard (default {DEFAULT_RTOL_EPS} machine "
                         f"epsilons = {DEFAULT_RTOL:.3g})")
    ap.add_argument("--out")
    args = ap.parse_args()

    try:
        with open(args.export, "r", encoding="utf-8") as f:
            doc = json.load(f)
        if not str(doc.get("schema", "")).startswith("gridsim-smallsignal/"):
            raise ValueError(f"not a small-signal export (schema={doc.get('schema')!r})")
    except Exception as e:  # noqa: BLE001
        print(f"crossval-smallsignal: setup error: {e}", file=sys.stderr)
        return 3   # N248: was 2 (Python's usage-error code); 3 = unreadable export, as the other sidecars
    # N245 (October 2026, the D-DOC-127 rule): a missing NumPy or SciPy is an ENVIRONMENT skip, exit 77 with the package
    # and the interpreter named (sidecar_common.missing). It used to be folded into the setup error above (exit 2, which
    # Python also uses for a usage error). The export is read first, so a bad input is never reported as a skip.
    loaded = missing("crossval_smallsignal", "numpy", "scipy.linalg")
    if loaded is None:
        return EXIT_SKIPPED
    np = loaded[0]
    eig = loaded[1].eig

    a = np.array(doc["aMatrix"], dtype=float)
    n = a.shape[0]

    # GridSim's eigenvalues (from the exported modes).
    gs_eig = np.array([complex(e["re"], e["im"]) for e in doc["eigenvalues"]])

    # SciPy reference: eigenvalues + left/right vectors for participation and the conditioning guard.
    w, vl, vr = eig(a, left=True, right=True)

    # cond(V) - the honest amplification of the eigenvalue disagreement for a non-normal A.
    condV = float(np.linalg.cond(vr))

    # N178: pair by optimal assignment (see pair_eigenvalues), not by sorting both lists.
    try:
        cols = pair_eigenvalues(gs_eig, w)
    except ValueError as e:
        print(f"crossval-smallsignal: setup error: {e}", file=sys.stderr)
        return 3   # N248: a count that does not pair is a setup error too; was 2
    sp_paired = w[cols]

    # Per-mode relative diff, with the conditioning guard folding into the accept threshold.
    scale = np.maximum(np.abs(sp_paired), 1.0)
    rel = np.abs(gs_eig - sp_paired) / scale
    guard = args.rtol * max(1.0, condV)
    max_rel = float(np.max(rel)) if n > 0 else 0.0
    eig_ok = bool(max_rel <= guard)
    worst = int(np.argmax(rel)) if n > 0 else None

    # Participation cross-check (magnitude form, normalised per mode), best-effort: SciPy modes in GridSim's order (the
    # eigenvalue pairing) against the exported participation. Non-blocking detail (reported, not gated)
    # because participation ordering/degeneracy is inherently cond-sensitive.
    part_max_diff = None
    # N180: an export from a defective state matrix says participationAvailable false; its participation rows are
    # then all-zero placeholders, so comparing them with SciPy's would report a meaningless difference. Skip and say why.
    participation_available = doc.get("participationAvailable", True)
    if doc.get("participation") and n > 0 and participation_available:
        try:
            # N178: SciPy's modes reordered into GridSim's mode order through the same pairing as the eigenvalues.
            vr_s = vr[:, cols]
            vl_s = vl[:, cols]
            p = np.abs(vl_s) * np.abs(vr_s)          # |psi_k phi_k|
            colsum = p.sum(axis=0, keepdims=True)
            colsum[colsum == 0] = 1.0
            p = (p / colsum).T                        # mode x state (GridSim's mode order), rows sum to 1
            gs_part = np.array(doc["participation"])
            part_max_diff = float(np.max(np.abs(p - gs_part)))
        except Exception:  # noqa: BLE001
            part_max_diff = None

    report = {
        "schema": "gridsim-crossval-smallsignal/1",
        "tool": "scipy.linalg.eig (LAPACK)",
        "case": doc.get("label"),
        "states": n,
        "inverterStatesIncluded": doc.get("inverterStatesIncluded"),
        "pairing": PAIRING_METHOD,
        "max_relative_eig_diff": max_rel,
        "worst_pair": None if worst is None else {
            "gridsim": [float(gs_eig[worst].real), float(gs_eig[worst].imag)],
            "scipy": [float(sp_paired[worst].real), float(sp_paired[worst].imag)],
        },
        "rtol": args.rtol,
        "condV": condV,
        "accept_threshold_rtol_times_condV": guard,
        "eigenvalues_within_tolerance": eig_ok,
        "participation_max_abs_diff": part_max_diff,
        "participation_compared": bool(participation_available and part_max_diff is not None),
        "participation_unavailable_reason": None if participation_available else doc.get("participationUnavailableReason"),
        "gridsim_exit_code": doc.get("exitCode"),
        "note": "non-symmetric eig agreement is cond(V)-limited, not machine-eps; "
                "condV ~ 1 means machine-eps-clean, condV >> 1 means conditioning-limited.",
    }
    text = json.dumps(report, indent=2)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(text)
    print(text)
    return 0 if eig_ok else 1


if __name__ == "__main__":
    sys.exit(main())
