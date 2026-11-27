"""WP35 independent reference for single-core cable constants (mpmath, 30 digits).

Formulas (written from the public literature, not from GridSim's code):
  * solid core surface impedance  z = rho m I0(m r) / (2 pi r I1(m r))                  (Schelkunoff 1934; Ametani 1980 eq. 3)
  * tube, inner radius a, outer b, D = I1(mb) K1(ma) - I1(ma) K1(mb):
      z_aa = rho m [I0(ma) K1(mb) + K0(ma) I1(mb)] / (2 pi a D)
      z_bb = rho m [I0(mb) K1(ma) + K0(mb) I1(ma)] / (2 pi b D)
      z_ab = rho / (2 pi a b D)                                                          (Ametani 1980 eqs. 4-6)
  * insulation   z = j w mu0 mu_r ln(ro/ri) / (2 pi);   P = ln(ro/ri) / (2 pi eps0 eps_r)
  * loop matrix -> conductor matrix  Zc = B^T Zloop B, B lower-triangular ones          (Ametani 1980 sec. 2)
  * earth return: Pollaczek (1931) integral, or Wedepohl & Wilcox (1973) closed form
      z = j w mu0/(2 pi) [ -ln(gamma m d / 2) + 1/2 - (2/3) m (h_i + h_j) ],  gamma = exp(Euler) = 1.7811
  * bonding: both ends = Kron elimination of sheaths/armour; single point = sheath rows dropped;
             cross-bonded = average over the three cyclic positions of the metallic layers, then Kron.
"""
import mpmath as mp
mp.mp.dps = 30
MU0 = 4 * mp.pi * mp.mpf('1e-7')
EPS0 = mp.mpf('8.8541878128e-12')
GAM = mp.exp(mp.euler)

def core_z(rho, mur, r, w):
    m = mp.sqrt(1j * w * MU0 * mur / rho)
    return rho * m * mp.besseli(0, m * r) / (2 * mp.pi * r * mp.besseli(1, m * r))

def tube_z(rho, mur, a, b, w):
    m = mp.sqrt(1j * w * MU0 * mur / rho)
    I0, I1, K0, K1 = (lambda x: mp.besseli(0, x)), (lambda x: mp.besseli(1, x)), (lambda x: mp.besselk(0, x)), (lambda x: mp.besselk(1, x))
    D = I1(m * b) * K1(m * a) - I1(m * a) * K1(m * b)
    zaa = rho * m * (I0(m * a) * K1(m * b) + K0(m * a) * I1(m * b)) / (2 * mp.pi * a * D)
    zbb = rho * m * (I0(m * b) * K1(m * a) + K0(m * b) * I1(m * a)) / (2 * mp.pi * b * D)
    zab = rho / (2 * mp.pi * a * b * D)
    return zaa, zbb, zab

def earth(w, rhoe, hi, hj, x, d, method):
    m = mp.sqrt(1j * w * MU0 / rhoe)
    H = hi + hj
    if method == 'wedepohl':
        return 1j * w * MU0 / (2 * mp.pi) * (-mp.log(GAM * m * d / 2) + mp.mpf(1) / 2 - mp.mpf(2) / 3 * m * H)
    D2 = mp.sqrt(x * x + H * H)
    g = lambda l: mp.exp(-H * mp.sqrt(l * l + m * m)) * mp.cos(l * x) / (l + mp.sqrt(l * l + m * m))
    am = abs(m)
    J = mp.quad(g, [0, am, 10 * am, 1 / H, 10 / H, 60 / H, mp.inf])
    return 1j * w * MU0 / (2 * mp.pi) * (mp.besselk(0, m * d) - mp.besselk(0, m * D2) + 2 * J)

def cable_internal(c, w):
    """c = dict(cond=[(ri, ro, rho, mur)], ins=[(eps_r, tand, mur)], R=outer radius). Returns conductor Z (internal+insulation), Y."""
    n = len(c['cond'])
    radii_out = [c['cond'][k + 1][0] if k + 1 < n else c['R'] for k in range(n)]
    zl = mp.matrix(n, n)
    for k in range(n):
        ri, ro, rho, mur = c['cond'][k]
        if ri == 0:
            zout = core_z(rho, mur, ro, w)
        else:
            zout = tube_z(rho, mur, ri, ro, w)[1]
        eps, tand, muri = c['ins'][k]
        zins = 1j * w * MU0 * muri * mp.log(radii_out[k] / ro) / (2 * mp.pi)
        zl[k, k] = zout + zins
        if k + 1 < n:
            ri2, ro2, rho2, mur2 = c['cond'][k + 1]
            zaa, zbb, zab = tube_z(rho2, mur2, ri2, ro2, w)
            zl[k, k] += zaa
            zl[k, k + 1] = -zab
            zl[k + 1, k] = -zab
    B = mp.matrix(n, n)
    for i in range(n):
        for j in range(i + 1):
            B[i, j] = 1
    Zc = B.T * zl * B
    y = []
    for k in range(n):
        eps, tand, muri = c['ins'][k]
        C = 2 * mp.pi * EPS0 * eps / mp.log(radii_out[k] / c['cond'][k][1])
        y.append(w * C * (tand + 1j))
    Y = mp.matrix(n, n)
    for k in range(n):
        Y[k, k] += y[k]
        if k + 1 < n:
            Y[k + 1, k + 1] += y[k]; Y[k, k + 1] -= y[k]; Y[k + 1, k] -= y[k]
    return Zc, Y

def system(cables, f, rhoe, method, bonding):
    w = 2 * mp.pi * f
    sizes = [len(c['cond']) for c in cables]
    N = sum(sizes)
    off = [sum(sizes[:i]) for i in range(len(cables))]
    Z = mp.matrix(N, N)
    for i, ci in enumerate(cables):
        Zc, _ = cable_internal(ci, w)
        for p in range(sizes[i]):
            for q in range(sizes[i]):
                Z[off[i] + p, off[i] + q] += Zc[p, q]
        for j, cj in enumerate(cables):
            if i == j:
                ze = earth(w, rhoe, ci['h'], ci['h'], 0, ci['R'], method)
            else:
                dx = ci['x'] - cj['x']; dy = ci['h'] - cj['h']
                ze = earth(w, rhoe, ci['h'], cj['h'], dx, mp.sqrt(dx * dx + dy * dy), method)
            for p in range(sizes[i]):
                for q in range(sizes[j]):
                    Z[off[i] + p, off[j] + q] += ze
    cores = [off[i] for i in range(len(cables))]
    metal = [k for k in range(N) if k not in cores]
    if bonding == 'cross':
        n = sizes[0]
        def perm(k, s):
            i = next(ii for ii in range(3) if off[ii] <= k < off[ii] + n); l = k - off[i]
            return k if l == 0 else off[(i + s) % 3] + l
        Za = mp.matrix(N, N)
        for s in range(3):
            for p in range(N):
                for q in range(N):
                    Za[p, q] += Z[perm(p, s), perm(q, s)]
        Z = Za / 3
    sub = lambda M, r, c: mp.matrix([[M[i, j] for j in c] for i in r])
    zpp = sub(Z, cores, cores)
    if bonding in ('both', 'cross') and metal:
        zpp = zpp - sub(Z, cores, metal) * mp.inverse(sub(Z, metal, metal)) * sub(Z, metal, cores)
    zs = sum(zpp[i, i] for i in range(3)) / 3
    zm = sum(zpp[i, j] for i in range(3) for j in range(3) if i != j) / 6
    return Z, zpp, zs - zm, zs + 2 * zm

def demo_cable(x, armour=False):
    cond = [(0, mp.mpf('0.0145'), mp.mpf('1.72e-8'), 1), (mp.mpf('0.0315'), mp.mpf('0.0325'), mp.mpf('2.83e-8'), 1)]
    ins = [(mp.mpf('2.5'), mp.mpf('0.0004'), 1), (mp.mpf('2.3'), 0, 1)]
    R = mp.mpf('0.0365')
    if armour:
        cond.append((mp.mpf('0.0365'), mp.mpf('0.0405'), mp.mpf('1.8e-7'), 10))
        ins.append((mp.mpf('2.3'), 0, 1))
        R = mp.mpf('0.0425')
    return dict(cond=cond, ins=ins, R=R, x=mp.mpf(x), h=mp.mpf('1.0'))

if __name__ == '__main__':
    for arm in (False, True):
        cables = [demo_cable('-0.1', arm), demo_cable('0', arm), demo_cable('0.1', arm)]
        for method in ('wedepohl', 'pollaczek'):
            for bond in ('both', 'single', 'cross'):
                Z, zpp, z1, z0 = system(cables, 50, 100, method, bond)
                print(f"armour={arm} {method:9s} {bond:6s} Z1 = ({mp.nstr(z1.real*1000,17)}, {mp.nstr(z1.imag*1000,17)})  Z0 = ({mp.nstr(z0.real*1000,17)}, {mp.nstr(z0.imag*1000,17)}) ohm/km")
