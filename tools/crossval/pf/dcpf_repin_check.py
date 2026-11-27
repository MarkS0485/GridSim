"""Check re-pinned DC results of a built GridSim against the NumPy makeBdc transcription (B33 re-pin proof).

Mark's ruling for B33 allows a pre-existing test whose DC result moves on a tapped, shifted or Gs case to be re-pinned only
with a maths proof: the new value must match MATPOWER's makeBdc conventions to 4096 eps. This script is that proof for
values taken from the built CLI's --json output. It imports dcpf_fidelity.py (the transcription) from this folder.

    python dcpf_repin_check.py <repo root> dcpf  <case .m under data/matpower> <gridsim dcpf --json output>
    python dcpf_repin_check.py <repo root> dcopf <case .m under data/matpower> <gridsim dcopf --json output>
    python dcpf_repin_check.py <repo root> lodf  <case .m under data/matpower> <gridsim lodf --branch k --json output>

dcpf:  every branch flow_mw and bus va_deg of the "branches" / "buses" tables against the transcription.
dcopf: the dispatch ("dispatch" table pg_mw, in-service generator order) is fed to the transcription; the flow of every
       "binding_lines" row must match it, and sum pg must equal sum Pd + sum Gs to the DC-OPF's balance tolerance (1e-6 MW).
       What the dispatch is (the LP's optimality) is not proven here: only that the reported numbers are the makeBdc model's.
lodf:  every "factors" row's lodf against column k of the makeBdc LODF (dcpf_fidelity.ptdf_lodf: PTDF = diag(b) (Binv(f,:)
       - Binv(t,:)) with Bbus reduced at the reference, phi = PTDF(:, f) - PTDF(:, t), LODF(:, k) = phi(:, k) / (1 - phi(k, k))),
       where k is the summary's "branch". Dimensionless; the floor is 1e-3.

Tolerance: |x - ref| <= 4096 eps max(||ref||_inf, 1e-3 pu), angles in radians, flows in per unit (the test suite's rule).
Exit 0 when every value passes, 1 otherwise; the largest observed ratio is printed.
"""
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dcpf_fidelity as d  # noqa: E402

EPS = 2.220446049250313e-16


def near(label, got, ref, floor):
    got, ref = np.asarray(got, float), np.asarray(ref, float)
    scale = max(float(np.max(np.abs(ref))) if ref.size else 0.0, floor)
    ratio = float(np.max(np.abs(got - ref))) / (EPS * scale) if ref.size else 0.0
    ok = ratio <= 4096
    print(f'{label}: largest |diff| = {ratio:.1f} eps of the norm (allowed 4096) {"PASS" if ok else "FAIL"}')
    return ok


def main():
    root, mode, rel, out = sys.argv[1:5]
    m, _ = d.load(os.path.join(root, 'data', 'matpower', rel))
    tables = json.load(open(out, encoding='utf-8'))['tables']
    base = m['base']
    ok = True
    if mode == 'dcpf':
        va, pf, _, _, _ = d.dcpf(m)
        ok &= near('flows', [r['flow_mw'] / base for r in tables['branches']], pf, 1e-3)
        ok &= near('angles', [r['va_deg'] * np.pi / 180 for r in tables['buses']], va, 1e-3)
    elif mode == 'dcopf':
        pg = [r['pg_mw'] for r in tables['dispatch']]
        assert len(pg) == len(m['gen']), 'dispatch rows do not align with the in-service generators'
        for g, p in zip(m['gen'], pg):
            g['pg'] = p
        demand = sum(b['pd'] for b in m['bus']) + sum(b['gs'] for b in m['bus'])
        bal = abs(sum(pg) - demand)
        print(f'balance: |sum Pg - (sum Pd + sum Gs)| = {bal:.3e} MW (allowed 1e-6) {"PASS" if bal <= 1e-6 else "FAIL"}')
        ok &= bal <= 1e-6
        _, pf, _, _, _ = d.dcpf(m)
        rows = tables.get('binding_lines', [])
        if rows:
            ok &= near('binding flows', [r['flow_mw'] / base for r in rows], [pf[r['branch']] for r in rows],
                       max(1e-3, float(np.max(np.abs(pf)))))
    elif mode == 'lodf':
        k = json.load(open(out, encoding='utf-8'))['summary']['branch']
        _, _, _, _, parts = d.dcpf(m)
        _, lodf = d.ptdf_lodf(m, parts)
        rows = tables['factors']
        ok &= near(f'lodf column {k} ({len(rows)} rows)', [r['lodf'] for r in rows], [lodf[r['branch'], k] for r in rows], 1e-3)
    else:
        raise SystemExit('mode must be dcpf, dcopf or lodf')
    sys.exit(0 if ok else 1)


if __name__ == '__main__':
    main()
