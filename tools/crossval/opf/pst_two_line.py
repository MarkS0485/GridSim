"""Reference values for the PST loss-minimisation test of the AC-OPF (Rc5Wp14OpfScopfTests, test 1).

Two buses, both held at |V| = 1 pu; bus 1 is the slack (angle 0); bus 2 draws 100 MW and 0 Mvar, and a reactive-only
unit at bus 2 supplies whatever reactive power the lines need there. Line A: z = 0.01 + j0.1 pu. Line B:
z = 0.02 + j0.1 pu with a phase shifter at its from end (bus 1), shift phi. Base 100 MVA, no line charging.

The branch model is MATPOWER's (manual, "Branch model"): t = tau e^{j phi}, Yff = ys / tau^2, Yft = -ys / conj(t),
Ytf = -ys / t, Ytt = ys, here with tau = 1. For each phi the bus-2 angle is the root of P2(theta2) = -1 pu (brentq);
the losses are the real part of the sum of the power entering every branch at both ends. The loss-minimising phi is
found by bounded scalar minimisation over [-30, 30] degrees, then polished as the root of the derivative. Only the standard library, NumPy and SciPy are used.

Run: python pst_two_line.py   (prints the figures the test pins)
"""
import cmath
import math

from scipy.optimize import brentq, minimize_scalar

BASE = 100.0
ZA = complex(0.01, 0.1)
ZB = complex(0.02, 0.1)


def branch_powers(z, phi, v1, v2):
    ys = 1 / z
    t = cmath.exp(1j * phi)
    yff, yft, ytf, ytt = ys, -ys / t.conjugate(), -ys / t, ys
    sf = v1 * (yff * v1 + yft * v2).conjugate()
    st = v2 * (ytf * v1 + ytt * v2).conjugate()
    return sf, st


def state(phi, th2):
    v1, v2 = 1.0 + 0j, cmath.exp(1j * th2)
    fa, ta = branch_powers(ZA, 0.0, v1, v2)
    fb, tb = branch_powers(ZB, phi, v1, v2)
    return fa, ta, fb, tb


def theta2(phi):
    # Power leaving bus 2 into the lines must equal its injection, -1 pu (the 100 MW load; the unit there has P = 0).
    return brentq(lambda th: (state(phi, th)[1] + state(phi, th)[3]).real + 1.0, -1.0, 1.0, xtol=1e-16, rtol=8.9e-16, maxiter=500)


def losses_mw(phi):
    fa, ta, fb, tb = state(phi, theta2(phi))
    return (fa + ta + fb + tb).real * BASE


def polish(phi0):
    import mpmath as mp
    mp.mp.dps = 40

    def inj(th2, ph):
        v1, v2 = mp.mpc(1), mp.expjpi(th2 / mp.pi)
        out = []
        for z, sh in ((mp.mpc(0.01, 0.1), mp.mpf(0)), (mp.mpc(0.02, 0.1), ph)):
            ys = 1 / z
            t = mp.expjpi(sh / mp.pi)
            sf = v1 * mp.conj(ys * v1 - ys / mp.conj(t) * v2)
            st = v2 * mp.conj(-ys / t * v1 + ys * v2)
            out.append((sf, st))
        return (out[0][0] + out[1][0]).real, (out[0][1] + out[1][1]).real

    p1 = lambda a, b: inj(a, b)[0]
    p2 = lambda a, b: inj(a, b)[1]

    def eqs(a, b):
        d1t, d1p = mp.diff(p1, (a, b), (1, 0)), mp.diff(p1, (a, b), (0, 1))
        d2t, d2p = mp.diff(p2, (a, b), (1, 0)), mp.diff(p2, (a, b), (0, 1))
        return [p2(a, b) + 1, d1t * d2p - d1p * d2t]

    th, ph = mp.findroot(eqs, (mp.mpf(theta2(phi0)), mp.mpf(phi0)))
    return ph


def main():
    res = minimize_scalar(losses_mw, bounds=(-math.radians(30), math.radians(30)), method="bounded",
                          options={"xatol": 1e-13, "maxiter": 1000})
    # The optimum is flat (the losses change by curvature/2 dphi^2), so the minimiser's phi is good to about 1e-6 deg
    # only. It is polished with mpmath at 40 digits: the optimum is where P2(theta2, phi) = -1 and the gradients of P1
    # and P2 are parallel (Lagrange: dP1/dth dP2/dphi - dP1/dphi dP2/dth = 0), solved by findroot from the SciPy point.
    phi = float(polish(res.x))
    h = 1e-4
    curv = (losses_mw(phi + h) - 2 * losses_mw(phi) + losses_mw(phi - h)) / h ** 2
    fa, ta, fb, tb = state(phi, theta2(phi))
    print("phi* deg            ", repr(math.degrees(phi)))
    print("losses at phi* MW   ", repr(losses_mw(phi)))
    print("losses at phi=0 MW  ", repr(losses_mw(0.0)))
    print("DC divider bound MW ", repr(ZA.real * ZB.real / (ZA.real + ZB.real) * BASE))
    print("curvature MW/rad^2  ", repr(curv))
    print("theta2 deg at phi*  ", repr(math.degrees(theta2(phi))))
    print("Q drawn by lines at bus 2 (Mvar)", repr(-(ta + tb).imag * BASE))
    print("P into line B at bus 1 (MW)     ", repr(fb.real * BASE))


if __name__ == "__main__":
    main()
