# Phase-1 (elastic) check of case30 rpo: the smallest uniform rating factor s for which the held-dispatch constraints
# have a solution (SciPy SLSQP from six starts). MATPOWER branch model, constant-power loads, every unit off the slack
# bus at its case Pg, the slack unit within Pmin .. Pmax, every Q, V and rating limit (ratings times s).
# Usage: python case30_rpo_phase1.py <path to case30.m>   (data/matpower/lib/case30.m in the repository)
# Result (SciPy 1.18.1): s = 1.0248577965 from every start that converged.
import re, sys, numpy as np
from scipy.optimize import minimize
txt = open(sys.argv[1]).read()
def mat(name):
    m = re.search(r'mpc\.' + name + r'\s*=\s*\[(.*?)\];', txt, re.S).group(1)
    rows = []
    for line in m.split('\n'):
        line = line.split('%')[0].strip().rstrip(';').strip()
        if line: rows.append([float(x) for x in line.replace(';', ' ').split()])
    return np.array(rows)
base = float(re.search(r'mpc\.baseMVA\s*=\s*([\d.]+)', txt).group(1))
bus, gen, br = mat('bus'), mat('gen'), mat('branch')
nb = len(bus); ids = {int(b[0]): i for i, b in enumerate(bus)}
Pd, Qd, Gs, Bs = bus[:, 2] / base, bus[:, 3] / base, bus[:, 4] / base, bus[:, 5] / base
Vmax, Vmin = bus[:, 11], bus[:, 12]
slack = [i for i in range(nb) if bus[i, 1] == 3][0]
gb = np.array([ids[int(g[0])] for g in gen]); ng = len(gen)
Pg0, Qmax, Qmin, Pmax, Pmin = gen[:, 1] / base, gen[:, 3] / base, gen[:, 4] / base, gen[:, 8] / base, gen[:, 9] / base
f = np.array([ids[int(b[0])] for b in br]); t = np.array([ids[int(b[1])] for b in br])
ys = 1 / (br[:, 2] + 1j * br[:, 3]); bc = br[:, 4]; rate = br[:, 5] / base
tap = np.where(br[:, 8] == 0, 1.0, br[:, 8]) * np.exp(1j * np.pi / 180 * br[:, 9])
Ytt = ys + 1j * bc / 2; Yff = Ytt / (tap * np.conj(tap)); Yft = -ys / np.conj(tap); Ytf = -ys / tap
Y = np.zeros((nb, nb), complex)
for k in range(len(br)):
    Y[f[k], f[k]] += Yff[k]; Y[t[k], t[k]] += Ytt[k]; Y[f[k], t[k]] += Yft[k]; Y[t[k], f[k]] += Ytf[k]
Y += np.diag(Gs + 1j * Bs)
rated = rate > 0
gslack = gb == slack
# x = [Va (nb), Vm (nb), Pg slack units, Qg (ng), s]
ns = int(gslack.sum())
def unpack(x):
    va, vm = x[:nb], x[nb:2 * nb]; ps = x[2 * nb:2 * nb + ns]; qg = x[2 * nb + ns:2 * nb + ns + ng]; s = x[-1]
    return va, vm, ps, qg, s
def V(x):
    va, vm, *_ = unpack(x); return vm * np.exp(1j * va)
def bal(x):
    va, vm, ps, qg, s = unpack(x); v = V(x); S = v * np.conj(Y @ v)
    pg = Pg0.copy(); pg[gslack] = ps
    Pinj = np.zeros(nb); Qinj = np.zeros(nb)
    np.add.at(Pinj, gb, pg); np.add.at(Qinj, gb, qg)
    return np.concatenate([S.real - (Pinj - Pd), S.imag - (Qinj - Qd), [va[slack]]])
def flows(x):
    v = V(x); s = unpack(x)[-1]
    sf = v[f] * np.conj(Yff * v[f] + Yft * v[t]); st = v[t] * np.conj(Ytf * v[f] + Ytt * v[t])
    lim = (rate * s) ** 2
    return np.concatenate([(lim - np.abs(sf) ** 2)[rated], (lim - np.abs(st) ** 2)[rated]])
lb = np.concatenate([-np.inf * np.ones(nb), Vmin, Pmin[gslack], Qmin, [0.0]])
ub = np.concatenate([np.inf * np.ones(nb), Vmax, Pmax[gslack], Qmax, [10.0]])
best = None
for start in range(6):
    rng = np.random.default_rng(start)
    x0 = np.concatenate([np.zeros(nb), np.ones(nb) + 0.02 * rng.standard_normal(nb) * (start > 0), Pg0[gslack], np.zeros(ng), [1.5]])
    x0 = np.clip(x0, lb, ub)
    r = minimize(lambda x: x[-1], x0, method='SLSQP', bounds=list(zip(lb, ub)),
                 constraints=[{'type': 'eq', 'fun': bal}, {'type': 'ineq', 'fun': flows}], options={'maxiter': 2000, 'ftol': 1e-14})
    feas = max(np.abs(bal(r.x)).max(), max(0, -flows(r.x).min()))
    print(f'start {start}: success {r.success} factor {r.x[-1]:.10f} feas {feas:.2e} {r.message}')
    if r.success and (best is None or r.x[-1] < best.x[-1]): best = r
x = best.x; v = V(x); va, vm, ps, qg, s = unpack(x)
sf = np.abs(v[f] * np.conj(Yff * v[f] + Yft * v[t])); st = np.abs(v[t] * np.conj(Ytf * v[f] + Ytt * v[t]))
print('minimum rating factor', repr(s))
for k in np.where(rated)[0]:
    if max(sf[k], st[k]) > rate[k] * s * (1 - 1e-6): print(f'  at rating: branch {k} ({int(br[k,0])}-{int(br[k,1])})')
for i in range(nb):
    if vm[i] <= Vmin[i] + 1e-7: print(f'  Vmin bus {int(bus[i,0])}')
    if vm[i] >= Vmax[i] - 1e-7: print(f'  Vmax bus {int(bus[i,0])}')
for g in range(ng):
    if qg[g] >= Qmax[g] - 1e-7: print(f'  Qmax gen {g}')
    if qg[g] <= Qmin[g] + 1e-7: print(f'  Qmin gen {g}')
