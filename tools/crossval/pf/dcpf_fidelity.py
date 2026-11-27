"""Independent reference for GridSim's DC power flow with taps, phase shifts and shunt conductance (B33).

This is a line-for-line NumPy transcription of MATPOWER's lib/makeBdc.m and the DC branch of lib/runpf.m / rundcpf
(the formulas only; no MATPOWER or GridSim code is run). Octave/MATLAB were not available, so rundcpf itself is
represented by this transcription:

    b      = stat ./ x                       % series susceptance, 0 out of service
    tap    = ones; tap(ratio ~= 0) = ratio    % TAP 0 means "a line"
    b      = b ./ tap
    Bbus   = Cft' * diag(b) * Cft
    Pfinj  = b .* (-shift * pi/180)
    Pbusinj = Cft' * Pfinj
    Pbus   = real(Sbus) - Pbusinj - Gs / baseMVA,   Sbus = (Pg - Pd) / baseMVA (in-service generators)
    Va(pvpq) = Bbus(pvpq, pvpq) \\ (Pbus(pvpq) - Bbus(pvpq, ref) * Va(ref))
    Pf     = (Bf * Va + Pfinj) * baseMVA,     Bf = diag(b) * Cft

The case is read as GridSim's MATPOWER reader keeps it: type-4 buses dropped, status-0 branches and branches touching a
dropped bus dropped, generators with status 0 left out. The reference angle is 0 (GridSim's convention; MATPOWER keeps the
file's VA of the reference bus, which shifts every angle by one constant and changes no flow). Each DC island gets GridSim's
reference (the island holding the first slack keeps it; another island takes its own first slack bus, else the bus of its
largest |Pg| generator, else its first bus).

Synthetic variants are made from the bundled files by the edits named in VARIANTS (the C# test applies the same edits).

Usage: python dcpf_fidelity.py <repo root> <output json>
Run with NumPy 2.5.3, SciPy 1.18.1 (pandapower not installed: its rundcpp was not run).
"""
import hashlib
import json
import math
import re
import sys

import numpy as np
import scipy
import scipy.sparse as sp
import scipy.sparse.linalg as spla


def num(x):
    try:
        return float(x)
    except ValueError:
        if not re.fullmatch(r'[0-9.eE+\-*/()a-z]+', x):
            raise
        return float(eval(x, {'__builtins__': {}}, {'sqrt': math.sqrt, 'pi': math.pi}))


def mat(txt, name):
    m = re.search(r'mpc\.' + name + r'\s*=\s*\[(.*?)\];', txt, re.S)
    rows = []
    for line in m.group(1).split('\n'):
        line = line.split('%')[0].strip()
        for part in line.split(';'):
            part = part.strip()
            if part:
                rows.append([num(x) for x in part.split()])
    return rows


def load(path):
    txt = open(path, encoding='utf-8').read()
    base = num(re.search(r'mpc\.baseMVA\s*=\s*([^;]+);', txt).group(1).strip())
    buses = [r for r in mat(txt, 'bus') if int(r[1]) != 4]
    live = {int(r[0]) for r in buses}
    gens = [r for r in mat(txt, 'gen') if r[7] > 0 and int(r[0]) in live]
    branches = [r for r in mat(txt, 'branch') if r[10] > 0 and int(r[0]) in live and int(r[1]) in live]
    model = {
        'base': base,
        'bus': [{'id': int(r[0]), 'type': int(r[1]), 'pd': r[2], 'gs': r[4]} for r in buses],
        'gen': [{'bus': int(r[0]), 'pg': r[1]} for r in gens],
        'br': [{'f': int(r[0]), 't': int(r[1]), 'x': r[3], 'ratio': r[8], 'shift': r[9]} for r in branches],
    }
    return model, hashlib.sha256(open(path, 'rb').read().replace(b'\r\n', b'\n')).hexdigest()   # LF-normalised


def references(m):
    n = len(m['bus'])
    idx = {b['id']: i for i, b in enumerate(m['bus'])}
    parent = list(range(n))

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x
    for br in m['br']:
        if br['x'] == 0:
            continue
        a, b = find(idx[br['f']]), find(idx[br['t']])
        if a != b:
            parent[a] = b
    comps = {}
    for i in range(n):
        comps.setdefault(find(i), []).append(i)
    slack = next((i for i in range(n) if m['bus'][i]['type'] == 3), 0)
    largest = {}
    for g in m['gen']:
        i = idx[g['bus']]
        if i not in largest or abs(g['pg']) > largest[i]:
            largest[i] = abs(g['pg'])
    refs = []
    for comp in comps.values():
        if slack in comp:
            refs.append(slack)
            continue
        r = next((i for i in comp if m['bus'][i]['type'] == 3), -1)
        if r < 0:
            best = -1.0
            for i in comp:
                if i in largest and largest[i] > best:
                    best, r = largest[i], i
        if r < 0:
            r = comp[0]
        refs.append(r)
    return sorted(refs)


def dcpf(m):
    """makeBdc + the rundcpf solve. Returns angles (rad), Pf (pu), and per-bus scheduled net injection (MW) with the
    reference entries replaced by the balancing injection, as GridSim's PInjMw."""
    base = m['base']
    n, nl = len(m['bus']), len(m['br'])
    idx = {b['id']: i for i, b in enumerate(m['bus'])}
    f = np.array([idx[br['f']] for br in m['br']], dtype=int)
    t = np.array([idx[br['t']] for br in m['br']], dtype=int)
    stat = np.ones(nl)
    b = stat / np.array([br['x'] for br in m['br']])
    tap = np.ones(nl)
    ratio = np.array([br['ratio'] for br in m['br']])
    i_ = np.nonzero(ratio)[0]
    tap[i_] = ratio[i_]
    b = b / tap
    cft = sp.csr_matrix((np.r_[np.ones(nl), -np.ones(nl)], (np.r_[np.arange(nl), np.arange(nl)], np.r_[f, t])), (nl, n))
    bf = sp.csr_matrix((np.r_[b, -b], (np.r_[np.arange(nl), np.arange(nl)], np.r_[f, t])), (nl, n))
    bbus = (cft.T @ bf).tocsc()
    shift = np.array([br['shift'] for br in m['br']])
    pfinj = b * (-shift * np.pi / 180)
    pbusinj = cft.T @ pfinj
    pg = np.zeros(n)
    for g in m['gen']:
        pg[idx[g['bus']]] += g['pg']
    pd = np.array([bb['pd'] for bb in m['bus']])
    gs = np.array([bb['gs'] for bb in m['bus']])
    sbus = (pg - pd) / base
    pbus = sbus - pbusinj - gs / base
    ref = references(m)
    pvpq = np.array([i for i in range(n) if i not in set(ref)], dtype=int)
    va = np.zeros(n)
    a = bbus[pvpq, :][:, pvpq]
    rhs = pbus[pvpq]   # Va(ref) = 0
    if n <= 400:
        va[pvpq] = np.linalg.solve(a.toarray(), rhs)
    else:
        va[pvpq] = spla.spsolve(a.tocsc(), rhs)
    pf = bf @ va + pfinj
    pinj = (pg - pd).copy()
    for r in ref:
        # MATPOWER: Pg(ref) += (Bbus(ref,:) * Va - Pbus(ref)) * baseMVA; the scheduled net injection follows.
        pinj[r] = pinj[r] + (float((bbus[[r], :] @ va)[0]) - pbus[r]) * base
    return va, pf, pinj, ref, (b, f, t, pfinj, bbus, pvpq, base)


def ptdf_lodf(m, parts):
    b, f, t, _, bbus, pvpq, _ = parts
    n, nl = len(m['bus']), len(m['br'])
    binv = np.zeros((n, n))
    binv[np.ix_(pvpq, pvpq)] = np.linalg.inv(bbus[pvpq, :][:, pvpq].toarray())
    ptdf = (b[:, None]) * (binv[f, :] - binv[t, :])          # branches x buses, inject at bus, withdraw at reference
    phi = ptdf[:, f] - ptdf[:, t]                             # flow on l per unit transfer along k
    lodf = np.full((nl, nl), np.nan)
    for k in range(nl):
        d = 1.0 - phi[k, k]
        if abs(d) >= 1e-9:
            lodf[:, k] = phi[:, k] / d
            lodf[k, k] = -1.0
    return ptdf, lodf


# Synthetic variants (bus ids / branch indices refer to the file's own numbering and the reader's branch order).
VARIANTS = {
    'case14-tap105': ('lib/case14.m', {'tap': {0: 1.05}}),
    'case9-shift5': ('lib/case9.m', {'shift': {5: 5.0}}),
    'case30-gs': ('lib/case30.m', {'gs': {3: 10.0, 7: 10.0, 21: 10.0}}),
    'case30-all': ('lib/case30.m', {'tap': {0: 1.05}, 'shift': {5: 5.0}, 'gs': {3: 10.0, 7: 10.0, 21: 10.0}}),
}
CASES = ['lib/case9.m', 'lib/case14.m', 'lib/case30.m', 'lib/case57.m', 'lib/case118.m', 'lib/case300.m',
         'benchmarks/cigre_mv.m', 'gb/GBreducednetwork.m']


def apply(m, edits):
    for k, v in edits.get('tap', {}).items():
        m['br'][k]['ratio'] = v
    for k, v in edits.get('shift', {}).items():
        m['br'][k]['shift'] = v
    for bid, v in edits.get('gs', {}).items():
        next(b for b in m['bus'] if b['id'] == bid)['gs'] = v
    return m


def g17(x):
    return float(format(float(x), '.17g'))


def entry(m, sha, edits=None):
    va, pf, pinj, ref, parts = dcpf(m)
    e = {
        'file_sha256': sha,
        'edits': edits or {},
        'base_mva': m['base'],
        'reference_bus_ids': [m['bus'][r]['id'] for r in ref],
        'bus_ids': [b['id'] for b in m['bus']],
        'va_rad': [g17(x) for x in va],
        'pf_pu': [g17(x) for x in pf],
        'p_inj_mw': [g17(x) for x in pinj],
        'census': {
            'taps': sum(1 for br in m['br'] if br['ratio'] not in (0.0, 1.0)),
            'shifts': sum(1 for br in m['br'] if br['shift'] != 0.0),
            'gs_buses': sum(1 for b in m['bus'] if b['gs'] != 0.0),
        },
    }
    return e, parts


def three_bus():
    """The spec's hand case: 1-2 x 0.1 shift 5 deg, 2-3 x 0.2, 1-3 x 0.2; Pd 60 MW at 3, Gs 10 MW at 2; base 100."""
    m = {'base': 100.0,
         'bus': [{'id': 1, 'type': 3, 'pd': 0.0, 'gs': 0.0}, {'id': 2, 'type': 1, 'pd': 0.0, 'gs': 10.0},
                 {'id': 3, 'type': 1, 'pd': 60.0, 'gs': 0.0}],
         'gen': [{'bus': 1, 'pg': 0.0}],
         'br': [{'f': 1, 't': 2, 'x': 0.1, 'ratio': 0.0, 'shift': 5.0}, {'f': 2, 't': 3, 'x': 0.2, 'ratio': 0.0, 'shift': 0.0},
                {'f': 1, 't': 3, 'x': 0.2, 'ratio': 0.0, 'shift': 0.0}]}
    return m


def main():
    root, out = sys.argv[1], sys.argv[2]
    data = root + '/data/matpower/'
    doc = {
        'header': {
            'script': 'tools/crossval/pf/dcpf_fidelity.py',
            'script_sha256': hashlib.sha256(open(__file__, 'rb').read()).hexdigest(),
            'numpy': np.__version__, 'scipy': scipy.__version__, 'pandapower': 'not installed (rundcpp not run)',
            'command': 'python tools/crossval/pf/dcpf_fidelity.py . tests/GridSim.Tests/fixtures/dcpf-fidelity/dcpf-fidelity-pins.json',
            'method': 'NumPy transcription of MATPOWER makeBdc + rundcpf; reference angle set to 0 (the file VA(ref) '
                      'is subtracted); dense LAPACK solve for <= 400 buses, SciPy spsolve above.',
            'units': 'va_rad radians, pf_pu per unit on base_mva (MW / base), p_inj_mw MW',
        },
        'cases': {},
    }
    for rel in CASES:
        m, sha = load(data + rel)
        e, parts = entry(m, sha)
        if rel == 'lib/case14.m':
            ptdf, lodf = ptdf_lodf(m, parts)
            e['ptdf_bus9'] = [g17(x) for x in ptdf[:, [b['id'] for b in m['bus']].index(9)]]
            e['lodf_branch3'] = [g17(x) for x in lodf[:, 3]]
        doc['cases'][rel] = e
    for name, (rel, edits) in VARIANTS.items():
        m, sha = load(data + rel)
        e, _ = entry(apply(m, edits), sha, {k: {str(a): b for a, b in v.items()} for k, v in edits.items()})
        e['source'] = rel
        doc['cases'][name] = e
    e, _ = entry(three_bus(), 'hand case (no file)')
    doc['cases']['three-bus-hand'] = e
    with open(out, 'w', encoding='utf-8', newline='\n') as fh:
        json.dump(doc, fh, indent=1)
        fh.write('\n')
    for k, v in doc['cases'].items():
        print(k, v['census'], 'refs', v['reference_bus_ids'])


if __name__ == '__main__':
    main()
