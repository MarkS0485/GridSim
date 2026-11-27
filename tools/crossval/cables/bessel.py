"""WP35 reference values: modified Bessel functions I0, I1, K0, K1 of complex argument.

SciPy (scipy.special.iv / kv, AMOS) and mpmath (40 digits) at the test points; the test pins the SciPy values
and asserts 1e-13 relative. The mpmath column shows how close SciPy itself is.
"""
import cmath, math
import mpmath as mp
from scipy.special import iv, kv

mp.mp.dps = 40
POINTS = [0.1 + 0.1j, 1 + 1j, 5 - 3j, 20 + 20j, 100 * cmath.exp(1j * math.pi / 4)]

for z in POINTS:
    zz = mp.mpc(z.real, z.imag)
    for name, s, m in (("I0", iv(0, z), mp.besseli(0, zz)), ("I1", iv(1, z), mp.besseli(1, zz)),
                       ("K0", kv(0, z), mp.besselk(0, zz)), ("K1", kv(1, z), mp.besselk(1, zz))):
        rel = abs(complex(m) - s) / abs(complex(m))
        print(f"z=({z.real!r}, {z.imag!r}) {name} = ({s.real!r}, {s.imag!r})  scipy-vs-mpmath {rel:.1e}")
