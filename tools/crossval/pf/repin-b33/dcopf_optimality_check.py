"""B33 re-pin proof for the security-constrained DC-OPF rows: balance, binding flows and optimality against makeBdc.

Input: a case export written by dcopf_export.fsx (this folder). The export holds the model as GridSim's reader keeps it
(bus Pd/Gs, branch x/tap/shift/RateA/in-service, generator Pmin/Pmax and the LP's cost per MWh) and GridSim's answer
(dispatch, binding lines, unrelievable lines, total cost). Everything numerical below is computed here, from the
MATPOWER makeBdc formulas, with NumPy / SciPy only:

    b = (1/x)/tap (tap 0 read as 1),  Bbus = Cft' diag(b) Cft,  Bf = diag(b) Cft,  Pfinj = b (-shift pi/180)
    Pf = (Bf Va + Pfinj) base

1. Data path (MATPOWER files only): the export's x, tap, shift, Pd, Gs and base equal what dcpf_fidelity.load reads from
   the .m file itself, so the model under test is the file's.
2. Balance: |sum Pg - (sum Pd + sum Gs)| <= 1e-6 MW (the DC-OPF's balance tolerance).
3. Binding flows: the makeBdc flows at GridSim's dispatch (dcpf_fidelity.dcpf) match every binding line's flow to
   4096 eps max(||Pf||_inf, 1e-3) per unit (the B33 rule).
4. Feasibility: every rated line the LP holds (RateA > 0, not reported unrelievable) carries |Pf| <= RateA + 1e-6 MW at
   GridSim's dispatch, and every unit sits inside [Pmin, Pmax].
5. Optimality: the same LP is built in the angle form (variables Pg and Va; Bbus Va base - Cg Pg = -(Pd + Gs) - Pbusinj base;
   -RateA <= (Bf Va + Pfinj) base <= RateA; Va(ref) = 0; Pmin <= Pg <= Pmax; minimise cost . Pg) and solved by
   scipy.optimize.linprog(method='highs'). The objectives must agree within

       tol = ||y||_1 (delta_gridsim + delta_highs) + 1e-7 ||Pg_highs||_1 + 4096 eps |z_highs|

   where y are HiGHS's duals, delta_* each solution's largest constraint violation measured in this makeBdc model (MW),
   1e-7 is HiGHS's default dual feasibility tolerance (a reduced-cost error of at most 1e-7 per MW of dispatch) and the
   last term is the rounding of the cost sum. By LP duality, a dispatch that is feasible within delta and whose cost is
   within tol of z_highs is optimal for the makeBdc LP to that tolerance. The LP may be degenerate (tied costs), so the
   dispatch itself need not equal HiGHS's; only the objective is compared.

    python dcopf_optimality_check.py <repo root> <export json> [<export json> ...]
Exit 0 when every check of every case passes.
"""
import json
import os
import sys

import numpy as np
import scipy
import scipy.sparse as sp
from scipy.optimize import linprog

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import dcpf_fidelity as d  # noqa: E402

EPS = 2.220446049250313e-16
TYPE = {'Slack': 3, 'Pv': 2, 'Pq': 1}


def model_of(e):
    return {
        'base': e['base'],
        'bus': [{'id': b['id'], 'type': TYPE[b['type']], 'pd': b['pd'], 'gs': b['gs']} for b in e['bus']],
        'gen': [{'bus': g['bus'], 'pg': g['pg']} for g in e['gen']],
        'br': [{'f': br['f'], 't': br['t'], 'x': br['x'], 'ratio': br['ratio'], 'shift': br['shift']}
               for br in e['br'] if br['on']],
    }, [i for i, br in enumerate(e['br']) if br['on']]


def check(root, path):
    e = json.load(open(path, encoding='utf-8'))
    name = e['case']
    print(f'== {name}')
    ok = True
    m, live = model_of(e)
    pos = {k: i for i, k in enumerate(live)}          # full branch index -> index among in-service branches
    base = m['base']

    # 1. data path
    if name.startswith('matpower/'):
        f, _ = d.load(os.path.join(root, 'data', name.replace('/', os.sep)))
        same = (f['base'] == base and len(f['bus']) == len(m['bus']) and len(f['br']) == len(m['br'])
                and all(a['id'] == b['id'] and a['pd'] == b['pd'] and a['gs'] == b['gs'] for a, b in zip(f['bus'], m['bus']))
                and all(a['f'] == b['f'] and a['t'] == b['t'] and a['x'] == b['x'] and (a['ratio'] or 1.0) == (b['ratio'] or 1.0)
                        and a['shift'] == b['shift'] for a, b in zip(f['br'], m['br'])))
        print(f'data: export equals the .m file read by dcpf_fidelity.load {"PASS" if same else "FAIL"}')
        ok &= same

    # 2. balance
    pg = np.array([g['pg'] for g in e['gen']])
    demand = sum(b['pd'] for b in m['bus']) + sum(b['gs'] for b in m['bus'])
    bal = abs(float(pg.sum()) - demand)
    print(f'balance: |sum Pg - (sum Pd + sum Gs)| = {bal:.3e} MW (allowed 1e-6) {"PASS" if bal <= 1e-6 else "FAIL"}')
    ok &= bal <= 1e-6

    # 3. binding flows
    va, pf, _, ref, (b, fi, ti, pfinj, bbus, _, _) = d.dcpf(m)
    rows = e['binding']
    if rows:
        scale = max(float(np.max(np.abs(pf))), 1e-3)
        got = np.array([r['flow'] / base for r in rows])
        want = np.array([pf[pos[r['branch']]] for r in rows])
        ratio = float(np.max(np.abs(got - want))) / (EPS * scale)
        print(f'binding flows ({len(rows)}): largest |diff| = {ratio:.1f} eps of the norm (allowed 4096) '
              f'{"PASS" if ratio <= 4096 else "FAIL"}')
        ok &= ratio <= 4096
    else:
        print('binding flows: none reported')

    # 4. feasibility at GridSim's dispatch
    unrel = set(e['unrelievable'])
    held = [k for k in live if e['br'][k]['rate'] > 0 and k not in unrel]
    rate = np.array([e['br'][k]['rate'] for k in held])
    flow = np.array([pf[pos[k]] * base for k in held])
    over = float(np.max(np.abs(flow) - rate)) if held else -np.inf
    pmin = np.array([g['pmin'] for g in e['gen']])
    pmax = np.array([g['pmax'] for g in e['gen']])
    bound = float(max(np.max(pmin - pg), np.max(pg - pmax)))
    delta_g = max(over, bound, bal, 0.0)
    print(f'feasibility: {len(held)} held lines, max(|Pf| - RateA) = {over:.3e} MW, bound violation {bound:.3e} MW '
          f'{"PASS" if over <= 1e-6 and bound <= 1e-9 else "FAIL"}')
    ok &= over <= 1e-6 and bound <= 1e-9

    # 5. optimality with HiGHS on the angle-form LP
    n, ng, nl = len(m['bus']), len(e['gen']), len(m['br'])
    idx = {bb['id']: i for i, bb in enumerate(m['bus'])}
    cg = sp.csr_matrix((np.ones(ng), ([idx[g['bus']] for g in e['gen']], np.arange(ng))), (n, ng))
    a_eq = sp.hstack([-cg, bbus * base]).tocsr()
    pd = np.array([bb['pd'] for bb in m['bus']])
    gs = np.array([bb['gs'] for bb in m['bus']])
    cft = sp.csr_matrix((np.r_[np.ones(nl), -np.ones(nl)], (np.r_[np.arange(nl), np.arange(nl)], np.r_[fi, ti])), (nl, n))
    pbusinj = cft.T @ pfinj
    b_eq = -(pd + gs) - pbusinj * base
    bf = sp.csr_matrix((np.r_[b, -b], (np.r_[np.arange(nl), np.arange(nl)], np.r_[fi, ti])), (nl, n))
    hp = [pos[k] for k in held]
    bfh = bf[hp, :] * base
    off = pfinj[hp] * base
    zero = sp.csr_matrix((len(hp), ng))
    a_ub = sp.vstack([sp.hstack([zero, bfh]), sp.hstack([zero, -bfh])]).tocsr()
    b_ub = np.r_[rate - off, rate + off]
    c = np.r_[[g['cost'] for g in e['gen']], np.zeros(n)]
    bounds = [(g['pmin'], None if g['pmax'] >= 1e299 else g['pmax']) for g in e['gen']]
    bounds += [(0.0, 0.0) if i in set(ref) else (None, None) for i in range(n)]
    res = linprog(c, A_ub=a_ub if hp else None, b_ub=b_ub if hp else None, A_eq=a_eq, b_eq=b_eq, bounds=bounds,
                  method='highs')
    if res.status != 0:
        print(f'optimality: HiGHS status {res.status} ({res.message}) FAIL')
        return False
    x = res.x
    r_eq = float(np.max(np.abs(a_eq @ x - b_eq)))
    r_ub = float(np.max(a_ub @ x - b_ub)) if hp else 0.0
    delta_h = max(r_eq, r_ub, 0.0)
    y = np.r_[res.eqlin.marginals, res.ineqlin.marginals if hp else []]
    z_h = float(res.fun)
    z_g = float(e['total_cost'])
    tol = float(np.sum(np.abs(y))) * (delta_g + delta_h) + 1e-7 * float(np.sum(np.abs(x[:ng]))) + 4096 * EPS * abs(z_h)
    diff = abs(z_g - z_h)
    print(f'optimality: GridSim cost {z_g!r}, HiGHS cost {z_h!r}, |diff| = {diff:.3e} (tol {tol:.3e}: '
          f'||y||_1 {np.sum(np.abs(y)):.3e}, delta_g {delta_g:.1e}, delta_h {delta_h:.1e}) '
          f'max |Pg_gridsim - Pg_highs| = {float(np.max(np.abs(pg - x[:ng]))):.3e} MW {"PASS" if diff <= tol else "FAIL"}')
    ok &= diff <= tol
    return ok


def main():
    root = sys.argv[1]
    print(f'NumPy {np.__version__}, SciPy {scipy.__version__} (HiGHS through scipy.optimize.linprog)')
    ok = all([check(root, p) for p in sys.argv[2:]])
    sys.exit(0 if ok else 1)


if __name__ == '__main__':
    main()
