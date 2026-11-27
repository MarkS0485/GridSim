"""Reference solutions for the fast-decoupled and Gauss-Seidel engines (Rc5Wp25FdlfGsQvModalTests, tests 3 and 8).

pandapower ships the PYPOWER ports of MATPOWER's makeYbus, makeB, fdpf and gausspf (pandapower.pypower). This script reads
a MATPOWER case file itself, so pandapower's network converter (and any change it makes to the data) is not involved,
and runs those functions on the case as it is:

* the start point is GridSim's default (a PV or slack bus at its generator set point, every angle and every PQ magnitude
  from the file);
* no reactive limits (pandapower's runpp default; the GridSim test turns them off too);
* tolerance 1e-12 (fdpf tests the mismatch divided by |V|, gausspf the mismatch itself).

It prints each algorithm's iteration count (for information: GridSim's convergence test differs from fdpf's) and the
solution |V| (pu) and angle (degrees) of every bus, in file order, as round-trip numbers. With --qv it also forms the QV
modal reduced Jacobian J_R = J_QV - J_Qt J_Pt^-1 J_PV at the solution (Newton's polar formulas, dP/dV and dQ/dV per unit
of |V|) and prints the eigenvalue of smallest magnitude from scipy.linalg.eig.

Usage: python fdlf_gs.py <case.m> [--qv]     (data/matpower/lib/case14.m and case30.m in the repository)
Run with pandapower 3.5.6, NumPy 2.5.3, SciPy 1.18.1.
"""
import re
import sys

import numpy as np
import scipy.linalg

from pandapower.pypower.fdpf import fdpf
from pandapower.pypower.gausspf import gausspf
from pandapower.pypower.makeB import makeB
from pandapower.pypower.makeYbus import makeYbus
from pandapower.pypower.ppoption import ppoption


def mat(txt, name):
    m = re.search(r'mpc\.' + name + r'\s*=\s*\[(.*?)\];', txt, re.S).group(1)
    rows = []
    for line in m.split('\n'):
        line = line.split('%')[0].strip().rstrip(';').strip()
        if line:
            rows.append([float(x) for x in line.replace(';', ' ').split()])
    return np.array(rows)


def main():
    path = sys.argv[1]
    txt = open(path).read()
    base = float(re.search(r'mpc\.baseMVA\s*=\s*([\d.]+)', txt).group(1))
    bus0, gen, br0 = mat(txt, 'bus'), mat(txt, 'gen'), mat(txt, 'branch')
    nb, nl = len(bus0), len(br0)
    ids = {int(b[0]): i for i, b in enumerate(bus0)}
    bus = np.zeros((nb, 18))
    bus[:, :13] = bus0[:, :13]
    bus[:, 0] = np.arange(nb)
    br = np.zeros((nl, 26))
    br[:, :13] = br0[:, :13]
    br[:, 0] = [ids[int(f)] for f in br0[:, 0]]
    br[:, 1] = [ids[int(t)] for t in br0[:, 1]]

    on = gen[:, 7] > 0
    sbus = np.zeros(nb, complex)
    vm = bus0[:, 7].copy()
    va = bus0[:, 8] * np.pi / 180
    for g in gen[on]:
        k = ids[int(g[0])]
        sbus[k] += (g[1] + 1j * g[2]) / base
        if bus0[k, 1] in (2, 3):
            vm[k] = g[5]
    sbus -= (bus0[:, 2] + 1j * bus0[:, 3]) / base
    ref = np.array([i for i in range(nb) if bus0[i, 1] == 3])
    pv = np.array([i for i in range(nb) if bus0[i, 1] == 2], dtype=int)
    pq = np.array([i for i in range(nb) if bus0[i, 1] == 1], dtype=int)
    v0 = vm * np.exp(1j * va)

    ybus = makeYbus(base, bus, br)[0].tocsr()
    opt = ppoption(PF_TOL=1e-12, PF_MAX_IT_FD=1000, PF_MAX_IT_GS=200000, VERBOSE=0)
    results = {}
    for name, alg in (('fdxb', 2), ('fdbx', 3)):
        bp, bpp = makeB(base, bus, br, alg)
        v, conv, it = fdpf(ybus, sbus.copy(), v0.copy(), bp, bpp, ref, pv, pq, opt)
        results[name] = (v, conv, it)
    v, conv, it = gausspf(ybus, sbus.copy(), v0.copy(), ref, pv, pq, opt)
    results['gs'] = (v, conv, it)
    for name, (v, conv, it) in results.items():
        print(f'{name}: converged {bool(conv)} in {it} iterations')
    v = results['fdxb'][0]
    for name, (w, _, _) in results.items():
        print(f'{name}: max |V - V(fdxb)| = {np.max(np.abs(w - v)):.3e}')
    print('vm = ' + ', '.join(repr(float(x)) for x in np.abs(v)))
    print('va_deg = ' + ', '.join(repr(float(x)) for x in np.angle(v) * 180 / np.pi))

    if '--qv' in sys.argv:
        y = ybus.toarray()
        g, b = y.real, y.imag
        m, th = np.abs(v), np.angle(v)
        s = v * np.conj(y @ v)
        p, q = s.real, s.imag
        pvpq = np.array(sorted(list(pv) + list(pq)))
        pqs = np.array(sorted(pq))
        def dth(i, j):
            return th[i] - th[j]
        jpt = np.zeros((len(pvpq), len(pvpq)))
        jpv = np.zeros((len(pvpq), len(pqs)))
        jqt = np.zeros((len(pqs), len(pvpq)))
        jqv = np.zeros((len(pqs), len(pqs)))
        for r, i in enumerate(pvpq):
            for c, j in enumerate(pvpq):
                jpt[r, c] = -q[i] - b[i, i] * m[i] ** 2 if i == j else m[i] * m[j] * (g[i, j] * np.sin(dth(i, j)) - b[i, j] * np.cos(dth(i, j)))
            for c, j in enumerate(pqs):
                jpv[r, c] = p[i] / m[i] + g[i, i] * m[i] if i == j else m[i] * (g[i, j] * np.cos(dth(i, j)) + b[i, j] * np.sin(dth(i, j)))
        for r, i in enumerate(pqs):
            for c, j in enumerate(pvpq):
                jqt[r, c] = p[i] - g[i, i] * m[i] ** 2 if i == j else -m[i] * m[j] * (g[i, j] * np.cos(dth(i, j)) + b[i, j] * np.sin(dth(i, j)))
            for c, j in enumerate(pqs):
                jqv[r, c] = q[i] / m[i] - b[i, i] * m[i] if i == j else m[i] * (g[i, j] * np.sin(dth(i, j)) - b[i, j] * np.cos(dth(i, j)))
        jr = jqv - jqt @ np.linalg.solve(jpt, jpv)
        w, vl, vr = scipy.linalg.eig(jr, left=True, right=True)
        k = int(np.argmin(np.abs(w)))
        part = np.conj(vl[:, k]) * vr[:, k]
        part = part / part.sum()
        print(f'qv: {len(pqs)} PQ buses; eigenvalues (sorted) = ' + ', '.join(repr(float(x.real)) for x in sorted(w, key=abs)))
        print(f'qv: smallest |lambda| = {w[k].real!r} (imag {w[k].imag!r})')
        print('qv: participation of the smallest mode (PQ bus id: value) = '
              + ', '.join(f'{int(bus0[i, 0])}: {part[r].real!r}' for r, i in enumerate(pqs)))


if __name__ == '__main__':
    main()
